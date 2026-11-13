"""Builds a ~6 minute test 'film' with real (synthetic TTS) speech at known times over music/noise.

Outputs into /out: film.wav (16 kHz mono), reference.srt (exact speech timings), film.mkv (video+audio).
Runs inside a Debian container with espeak-ng + ffmpeg.
"""
import random
import subprocess
import wave
import os

random.seed(7)
OUT = "/out"
SENTENCES = [
    ("de", "Wir müssen sofort zurück zur Brücke."),
    ("en", "I told you this would happen, didn't I?"),
    ("de", "Hör auf damit, das bringt doch nichts."),
    ("en", "The ship is losing power on every deck."),
    ("de", "Ich weiß nicht, ob wir das schaffen."),
    ("en", "Listen to me. We only get one chance."),
    ("de", "Wo bist du die ganze Nacht gewesen?"),
    ("en", "That is none of your business."),
    ("de", "Der Plan war von Anfang an zum Scheitern verurteilt."),
    ("en", "Maybe, but it was the only plan we had."),
    ("de", "Bleib hier und warte auf mein Zeichen."),
    ("en", "Three minutes. That's all I need."),
    ("de", "Das Wetter wird morgen deutlich besser."),
    ("en", "Nobody comes back from the northern mountains."),
    ("de", "Kannst du mir bitte helfen, die Kisten zu tragen?"),
    ("en", "Of course. Where do you want them?"),
    ("de", "Ganz hinten, neben dem alten Schrank."),
    ("en", "Do you hear that? Something is moving."),
    ("de", "Das ist nur der Wind, beruhige dich."),
    ("en", "I have a really bad feeling about this place."),
    ("de", "Morgen fahren wir in die Stadt und kaufen ein."),
    ("en", "Promise me you will be careful out there."),
    ("de", "Ich verspreche es dir."),
    ("en", "Good. Then let's go before it gets dark."),
    ("de", "Warte, ich habe meine Jacke vergessen."),
    ("en", "Hurry up, we are already late."),
    ("de", "Wie viele Leute kommen heute Abend?"),
    ("en", "About twenty, if everyone shows up."),
    ("de", "Dann brauchen wir mehr Stühle."),
    ("en", "I will ask the neighbours tomorrow morning."),
    ("de", "Vergiss nicht, die Tür abzuschließen."),
    ("en", "Who left the lights on in the basement?"),
    ("de", "Das war bestimmt wieder dein Bruder."),
    ("en", "He never listens to anyone."),
    ("de", "Am Ende wird alles gut, glaub mir."),
    ("en", "I really hope you are right about that."),
]


def tts(lang, text, path):
    voice = "de" if lang == "de" else "en-us"
    subprocess.run(["espeak-ng", "-v", voice, "-s", str(random.randint(150, 185)), "-w", path + ".raw.wav", text], check=True)
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", path + ".raw.wav", "-ar", "16000", "-ac", "1", path], check=True)


def duration(path):
    with wave.open(path) as w:
        return w.getnframes() / w.getframerate()


def fmt(t):
    ms = int(round(t * 1000))
    h, ms = divmod(ms, 3600000)
    m, ms = divmod(ms, 60000)
    s, ms = divmod(ms, 1000)
    return f"{h:02}:{m:02}:{s:02},{ms:03}"


os.makedirs(OUT + "/clips", exist_ok=True)
t = 4.0
cues = []
inputs = []
filters = []
for i, (lang, text) in enumerate(SENTENCES):
    clip = f"{OUT}/clips/{i:02}.wav"
    tts(lang, text, clip)
    d = duration(clip)
    cues.append((t, t + d, text))
    inputs += ["-i", clip]
    filters.append(f"[{i + 2}:a]adelay={int(t * 1000)}|{int(t * 1000)}[s{i}]")
    t += d + random.uniform(0.8, 6.0)
    if i in (11, 23):
        t += 25.0  # long music-only stretch (no speech) – classic VAD trap

total = t + 5.0
# 1: pink noise bed, 0: "music" (chords + tremolo) – speech-free background
pre = [
    "-f", "lavfi", "-i", f"sine=frequency=220:duration={total},volume=0.10",
    "-f", "lavfi", "-i", f"anoisesrc=color=pink:amplitude=0.04:duration={total}",
]
mix = ";".join(filters) + ";" + "".join(f"[s{i}]" for i in range(len(SENTENCES))) + \
    f"[0:a][1:a]amix=inputs={len(SENTENCES) + 2}:normalize=0:duration=longest[a]"
subprocess.run(["ffmpeg", "-loglevel", "error", "-y", *pre, *inputs, "-filter_complex", mix, "-map", "[a]",
                "-ar", "16000", "-ac", "1", f"{OUT}/film.wav"], check=True)
subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", f"testsrc2=size=640x360:rate=24:duration={total}",
                "-i", f"{OUT}/film.wav", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "35", "-c:a", "aac", "-shortest",
                f"{OUT}/film.mkv"], check=True)

with open(f"{OUT}/reference.srt", "w", encoding="utf-8") as f:
    for n, (a, b, text) in enumerate(cues, 1):
        f.write(f"{n}\n{fmt(a)} --> {fmt(b)}\n{text}\n\n")
print(f"film {total:.1f}s, {len(cues)} cues")
