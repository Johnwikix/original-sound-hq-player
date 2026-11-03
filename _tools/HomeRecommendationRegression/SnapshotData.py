"""Create read-only SQLite backups for repeatable local recommendation evaluation."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import sqlite3

parser = argparse.ArgumentParser()
parser.add_argument("library", type=Path)
parser.add_argument("features", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--settings", type=Path)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
manifest = {"NowUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "ArtistSplitSymbols": ", ; / 、 & feat. :", "Sha256": {}}
if args.settings:
    settings = json.loads(args.settings.read_text(encoding="utf-8-sig"))
    manifest["ArtistSplitSymbols"] = settings.get("ArtistSplitSymbols", manifest["ArtistSplitSymbols"])
for source, name in ((args.library, "library.db"), (args.features, "features.db")):
    destination = args.output / name
    if destination.exists():
        raise FileExistsError(f"Use a new snapshot directory: {destination}")
    with sqlite3.connect(source.resolve().as_uri() + "?mode=ro", uri=True) as reader:
        with sqlite3.connect(destination) as writer:
            reader.backup(writer)
    with destination.open("rb") as stream:
        manifest["Sha256"][name] = hashlib.file_digest(stream, "sha256").hexdigest()
(args.output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2),
                                        encoding="utf-8", newline="\r\n")
with sqlite3.connect(args.output / "library.db") as db:
    print("tracks=", db.execute("SELECT COUNT(*) FROM Music").fetchone()[0])
    print("history=", db.execute("SELECT COUNT(*) FROM PlaybackHistory").fetchone()[0])
with sqlite3.connect(args.output / "features.db") as db:
    print("analysis=", db.execute("SELECT COUNT(*) FROM AnalysisRecord WHERE Version=2 AND length(Data)=1536").fetchone()[0])
