using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.SubtitleForge.Translation;

/// <summary>
/// Minimal SentencePiece unigram tokenizer (reads the .spm protobuf directly, Viterbi segmentation).
/// Normalisation approximates "nmt_nfkc": Unicode NFKC, collapsed whitespace, "▁" as word boundary.
/// </summary>
public sealed class SentencePiece
{
    /// <summary>Word boundary marker.</summary>
    public const char Boundary = '▁';

    private readonly Dictionary<string, float> _scores = new(StringComparer.Ordinal);
    private readonly int _maxPieceLength;
    private readonly float _unknownScore;

    private SentencePiece(List<(string Piece, float Score, int Type)> pieces)
    {
        var min = 0f;
        foreach (var (piece, score, type) in pieces)
        {
            // type 1 = normal, 4 = user defined; skip control/unknown/byte pieces
            if (type is 1 or 4 or 0)
            {
                _scores[piece] = score;
                _maxPieceLength = Math.Max(_maxPieceLength, piece.Length);
                min = Math.Min(min, score);
            }
        }

        _unknownScore = min - 10f;
    }

    /// <summary>
    /// Loads a .spm model file.
    /// </summary>
    /// <param name="path">Path.</param>
    /// <returns>Tokenizer.</returns>
    public static SentencePiece Load(string path)
    {
        var data = File.ReadAllBytes(path);
        var pieces = new List<(string, float, int)>();
        var pos = 0;
        while (pos < data.Length)
        {
            var (field, wire) = ReadTag(data, ref pos);
            if (field == 1 && wire == 2)
            {
                var len = (int)ReadVarint(data, ref pos);
                pieces.Add(ParsePiece(data, pos, len));
                pos += len;
            }
            else
            {
                Skip(data, ref pos, wire);
            }
        }

        return new SentencePiece(pieces);
    }

    /// <summary>
    /// Normalises and splits text into pieces.
    /// </summary>
    /// <param name="text">Text.</param>
    /// <returns>Pieces.</returns>
    public List<string> Encode(string text)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return new List<string>();
        }

        var n = normalized.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (var end = 1; end <= n; end++)
        {
            for (var len = 1; len <= Math.Min(_maxPieceLength, end); len++)
            {
                var start = end - len;
                if (float.IsNegativeInfinity(best[start]))
                {
                    continue;
                }

                if (_scores.TryGetValue(normalized.Substring(start, len), out var score) && best[start] + score > best[end])
                {
                    best[end] = best[start] + score;
                    from[end] = start;
                }
            }

            // Unknown single character fallback.
            if (float.IsNegativeInfinity(best[end]) || best[end - 1] + _unknownScore > best[end])
            {
                if (!float.IsNegativeInfinity(best[end - 1]) && best[end - 1] + _unknownScore > best[end])
                {
                    best[end] = best[end - 1] + _unknownScore;
                    from[end] = end - 1;
                }
            }
        }

        var result = new List<string>();
        for (var end = n; end > 0; end = from[end])
        {
            result.Add(normalized[from[end]..end]);
        }

        result.Reverse();
        return result;
    }

    /// <summary>
    /// Joins pieces back into text.
    /// </summary>
    /// <param name="pieces">Pieces.</param>
    /// <returns>Text.</returns>
    public static string Decode(IEnumerable<string> pieces)
    {
        var sb = new StringBuilder();
        foreach (var p in pieces)
        {
            sb.Append(p);
        }

        return sb.ToString().Replace(Boundary, ' ').Trim();
    }

    private static string Normalize(string text)
    {
        var t = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(t.Length + 1);
        sb.Append(Boundary);
        var lastWasSpace = true;
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(Boundary);
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        while (sb.Length > 1 && sb[^1] == Boundary)
        {
            sb.Length--;
        }

        return sb.Length == 1 ? string.Empty : sb.ToString();
    }

    private static (string, float, int) ParsePiece(byte[] data, int start, int length)
    {
        string piece = string.Empty;
        float score = 0;
        var type = 1;
        var pos = start;
        while (pos < start + length)
        {
            var (field, wire) = ReadTag(data, ref pos);
            switch (field)
            {
                case 1 when wire == 2:
                    var len = (int)ReadVarint(data, ref pos);
                    piece = Encoding.UTF8.GetString(data, pos, len);
                    pos += len;
                    break;
                case 2 when wire == 5:
                    score = BitConverter.ToSingle(data, pos);
                    pos += 4;
                    break;
                case 3 when wire == 0:
                    type = (int)ReadVarint(data, ref pos);
                    break;
                default:
                    Skip(data, ref pos, wire);
                    break;
            }
        }

        return (piece, score, type);
    }

    private static (int Field, int Wire) ReadTag(byte[] data, ref int pos)
    {
        var tag = ReadVarint(data, ref pos);
        return ((int)(tag >> 3), (int)(tag & 7));
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    private static void Skip(byte[] data, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(data, ref pos); break;
            case 1: pos += 8; break;
            case 2:
                // Read the length first: "pos += ReadVarint(ref pos)" would add to the old position.
                var length = (int)ReadVarint(data, ref pos);
                pos += length;
                break;
            case 5: pos += 4; break;
            default: throw new InvalidDataException("Unsupported protobuf wire type " + wire);
        }
    }
}
