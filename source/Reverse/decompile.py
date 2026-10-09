# Decompiled C of game functions, from the Ghidra project (ghidra/analyze.cmd made it), with names.txt applied first.
#   python decompile.py <rva>...      -> .cache/decomp/<rva>.c for each, printed too (one Ghidra start for all)
#   an "@rva" argument names the function holding that address instead of one starting at it
import os, re, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, ".cache", "decomp")
GHIDRA = r"C:\Tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat"
JAVA_HOME = r"C:\Tools\jdk-21.0.12.1+1"
PROJECT = r"C:\Tools\ghidra-projects"


def decompile(rvas, quiet=False):
    os.makedirs(OUT, exist_ok=True)
    out = os.path.join(OUT, "batch.c")
    env = dict(os.environ, JAVA_HOME=JAVA_HOME, PATH=JAVA_HOME + r"\bin;" + os.environ["PATH"], GHIDRA_HEADLESS_MAXMEM="16G")
    cmd = [GHIDRA, PROJECT, "San14", "-process", "SAN14CE_KO.mem.exe", "-noanalysis", "-readOnly",
           "-scriptPath", os.path.join(HERE, "ghidra"), "-postScript", "Decompile.java",
           os.path.join(HERE, "names.txt"), out] + [r if isinstance(r, str) else f"{r:#x}" for r in rvas]
    p = subprocess.run(cmd, env=env, capture_output=True, text=True, stdin=subprocess.DEVNULL)
    if not os.path.exists(out) or p.returncode != 0:
        print(p.stdout[-3000:], p.stderr[-2000:])
        raise SystemExit("decompile failed")
    for line in p.stdout.splitlines():
        if "Decompile.java>" in line: print(line.split("Decompile.java>", 1)[1].strip(), file=sys.stderr)
    text = open(out, encoding="utf-8").read()
    # Ghidra's names carry the run's absolute addresses: show RVAs instead (FUN_r2b2da0, DAT_r1a4a504 = value).
    from image import Image
    im = Image()
    def to_rva(m):
        rva = int(m.group(2), 16) - im.base
        if m.group(1) == "DAT" and im.sections[".data"][0] <= rva < im.sections[".data"][0] + im.sections[".data"][1]:
            return f"DAT_r{rva:x}"
        return f"{m.group(1)}_r{rva:x}"
    text = re.sub(r"\b(FUN|DAT|LAB|PTR_DAT|PTR_FUN|PTR|switchD|caseD)_([0-9a-f]{12})\b", to_rva, text)
    text = re.sub(r"\b(ram,)?0x(7ff7[0-9a-f]{8})\b", lambda m: f"rva:{int(m.group(2), 16) - im.base:#x}", text)
    for part in text.split("// ===== rva ")[1:]:
        rva = part.split()[0]
        open(os.path.join(OUT, f"{rva}.c"), "w", encoding="utf-8").write("// ===== rva " + part)
    if not quiet:
        sys.stdout.reconfigure(encoding="utf-8")
        print(text)
    return text


if __name__ == "__main__":
    decompile([a if a.startswith("@") else int(a, 16) for a in sys.argv[1:]])
