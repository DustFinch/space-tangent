# SPDX-License-Identifier: GPL-2.0-or-later
"""Prepare the pinned HYG subset and copy the separately licensed figure data."""

import argparse
import csv
import hashlib
from pathlib import Path
import shutil

HYG_SHA256 = "d9f69fd86bbf90a4e4d52b4c5c53eacfa6dfc0bfdef85bfd94f095e0bebe4ebd"
FIGURES_SHA256 = "294f66bef5d5cf50b1e17f16d2efa1d97a15131612c68dd935adef6e7373e13c"
COLUMNS = ("hip", "name", "ra_rad", "dec_rad", "mu_alpha_cos_delta_rad_y", "mu_delta_rad_y", "v_mag")


def prepare(source, destination):
    hyg = source / "hyg-v41.csv"
    figures = source / "constellation-lines.json"
    for path, expected in ((hyg, HYG_SHA256), (figures, FIGURES_SHA256)):
        with path.open("rb") as file:
            actual = hashlib.file_digest(file, "sha256").hexdigest()
        if actual != expected:
            raise ValueError(f"Unexpected SHA-256 for {path}")

    destination.mkdir(parents=True, exist_ok=True)
    with hyg.open(encoding="utf-8", newline="") as source_file, (destination / "bright-stars.tsv").open(
        "w", encoding="utf-8", newline=""
    ) as target:
        reader = csv.DictReader(source_file)
        writer = csv.writer(target, delimiter="\t", lineterminator="\n", quoting=csv.QUOTE_NONE)
        writer.writerow(COLUMNS)
        for row in reader:
            if row["hip"] and row["mag"] and float(row["mag"]) <= 6:
                writer.writerow([row[column] for column in ("hip", "proper", "rarad", "decrad", "pmrarad", "pmdecrad", "mag")])
    shutil.copyfile(figures, destination / figures.name)
    for name in ("HYG-LICENSE.txt", "LINES-LICENSE.txt"):
        shutil.copyfile(source / name, destination / name)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    prepare(args.source, args.destination)
