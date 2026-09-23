"""Install the Lab 1 scaffold or restore the complete checkpoint."""

import argparse
from pathlib import Path
import shutil


ROOT = Path(__file__).resolve().parent
SCAFFOLD = ROOT / ".kurs" / "scaffolds" / "lab1" / "Pipeline.cs"
CHECKPOINT = ROOT / ".kurs" / "checkpoints" / "lab1" / "Pipeline.cs"
TARGET = ROOT / "src" / "Worker" / "Pipeline.cs"
BACKUP = ROOT / ".kurs" / "Pipeline.forsok.cs"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--checkpoint",
        action="store_true",
        help="Restore the complete solution instead of the participant scaffold.",
    )
    args = parser.parse_args()

    source = CHECKPOINT if args.checkpoint else SCAFFOLD
    if not source.is_file():
        raise SystemExit(
            f"Fant ikke Lab 1-filen: {source}\n"
            "Pakk ut mini-nils.zip på nytt, eller kjør scripts/lag-starter-zip.py først."
        )

    if not TARGET.is_file():
        raise SystemExit(f"Fant ikke pipeline-filen: {TARGET}")

    BACKUP.parent.mkdir(parents=True, exist_ok=True)
    # Do not preserve the source timestamp: incremental dotnet builds use
    # timestamps and could otherwise keep running the previous Worker.dll.
    shutil.copyfile(TARGET, BACKUP)
    shutil.copyfile(source, TARGET)

    print("Lab 1-skjelettet er klart." if not args.checkpoint else "Lab 1-sjekkpunktet er lagt inn.")
    print(f"Det forrige forsøket er lagret i {BACKUP.relative_to(ROOT)}.")
    print("Kjør: dotnet build")


if __name__ == "__main__":
    main()
