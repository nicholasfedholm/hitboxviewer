"""Build and stage hitboxviewer: dotnet build (Release), zip to <work>/staged, testzip, sha256.
Never installs anything. Usage: python -I build.py"""
import hashlib, json, pathlib, subprocess, sys, zipfile

SRC = pathlib.Path(__file__).resolve().parent / "HitboxViewer"
WORK = SRC.parent.parent
info = json.loads((SRC / "modinfo.json").read_text(encoding="utf-8"))
out = WORK / "staged" / f"{info['modid']}_{info['version']}.zip"

r = subprocess.run(["dotnet", "build", "-c", "Release", "-nologo", "--no-incremental"], cwd=SRC, capture_output=True, text=True)
tail = [l for l in r.stdout.splitlines() if "Warning(s)" in l or "Error(s)" in l]
print("build exit", r.returncode, "|", " ".join(t.strip() for t in tail))
if r.returncode != 0:
    sys.exit(1)

out.parent.mkdir(exist_ok=True)
files = {"modinfo.json": SRC / "modinfo.json",
         "HitboxViewer.dll": SRC / "bin" / "Release" / "HitboxViewer.dll",
         "HitboxViewer.pdb": SRC / "bin" / "Release" / "HitboxViewer.pdb"}
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for arc, p in files.items():
        z.write(p, arc)
with zipfile.ZipFile(out) as z:
    bad = z.testzip()
    names = z.namelist()
sha = hashlib.sha256(out.read_bytes()).hexdigest()
print("staged", out.name, out.stat().st_size, "bytes; entries", names)
print("testzip", "OK" if bad is None else f"BAD {bad}")
print("sha256", sha)
