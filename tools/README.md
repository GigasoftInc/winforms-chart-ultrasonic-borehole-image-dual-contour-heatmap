# Tools — UBI Data Pipeline

The `TestData_FORGE_*.txt` files in the repo root are pre-processed; they're committed to the repo so the chart sample builds and runs without external dependencies. This folder contains the Python pipeline that generated them, for anyone who wants to:

- Regenerate the TestData files from the original Schlumberger DLIS source
- Adjust the depth stride (defaults to 12 → 0.4 ft/row)
- Apply the same pipeline to a different UBI dataset
- Understand what the TestData format encodes

## Pipeline overview

```
Schlumberger DLIS source file
        │
        │  1. extract_azimuthal.py
        │     - Reads UBI Frame 4B (HF pass)
        │     - Extracts AWCN and TTCN channels
        │     - Bins into 180 azimuth sectors (2° each)
        │     - Strides depth (default 12 → 0.4 ft)
        │     - Writes UTF-16 LE TSV
        ▼
TestData_FORGE_AWCN.txt  /  TestData_FORGE_TTCN.txt
        │
        │  2. clip_null_filter.py
        │     - Coerces out-of-range sentinels (-500 "Absent", etc) to nulls
        │     - Clips valid values to display range (0..30 AWCN, 0..50 TTCN)
        │     - Empty value column → null marker for downstream loader
        ▼
TestData_FORGE_AWCN_clipped.txt  /  TestData_FORGE_TTCN_clipped.txt
```

The chart application reads the `_clipped.txt` files directly.

---

## Prerequisites

- Python 3.10 or newer
- `dlisio` — DLIS file reader by Equinor
- `numpy`

```bash
pip install dlisio numpy
```

---

## Source data

Original DLIS file: **Utah FORGE Geothermal Data Repository**
- Well: FORGE 16A(78)-32
- Run: 3B (UBI HF pass, December 2020)
- Citation: McLennan, J. (2021). *Utah FORGE: Well 16A(78)-32 Logging Data — Run 3B*. DOI [10.15121/1814488](https://doi.org/10.15121/1814488).
- License: CC-BY 4.0

Download the DLIS from the Geothermal Data Repository at the DOI above. Look for the file containing the **Run 3B HF pass** (~hundreds of MB). The exact filename varies by archive packaging.

---

## Step 1 — `extract_azimuthal.py`

Extracts AWCN and TTCN channels from the source DLIS.

```bash
python extract_azimuthal.py \
    --input  /path/to/Run3B.dlis \
    --output ../TestData_FORGE_AWCN.txt \
    --channel AWCN \
    --frame 4B \
    --stride 12 \
    --depth-min 5077.13 \
    --depth-max 7099.13
```

Run twice — once for `AWCN`, once for `TTCN` — producing two TestData files.

**Output format** (matches the chart loader's expectations):

- UTF-16 LE encoding with BOM
- Tab-separated, CRLF line endings
- Three columns: `azimuth_deg`, `depth_ft`, `value`
- Outer loop: depth ascending. Inner loop: azimuth ascending (1°, 3°, ..., 359°)
- Empty `azimuth_deg` cell when value is exactly 0 (extractor convention; safe to ignore)
- Total rows per file: `nDepths × 180`

For our demo dataset at stride 12: 5056 depths × 180 azimuths = 910,080 rows per file, ~35MB per file before clip-filtering.

**Stride rationale.** Source UBI is sampled at 0.4 inches (~0.0333 ft) depth. Stride 12 → ~0.4 ft/row, which is sufficient density for chart display at full-zoom (the contour-interpolation gaps between rows are sub-pixel at typical viewport sizes). Smaller strides produce larger files; if you want denser data for deep-zoom legibility, drop the stride to 6 or 4.

---

## Step 2 — `clip_null_filter.py`

Post-processes the raw TestData files to standardize the null-cell marker and clip valid values to the chart's display range.

```bash
python clip_null_filter.py \
    --input  ../TestData_FORGE_AWCN.txt \
    --output ../TestData_FORGE_AWCN_clipped.txt \
    --clip-min 0 \
    --clip-max 30
```

Run twice — once for AWCN with `--clip-max 30`, once for TTCN with `--clip-max 50`.

**What this step does:**

- DLIS files leak Schlumberger sentinel values (`-500.0` "Absent", occasional extreme negatives from parsing artifacts) into the value column. These aren't real measurements — they're the file format's way of saying "no echo this fire." We coerce them to **empty strings** so the downstream chart loader maps them to `NULL_VALUE_IN_Z_GRID` (= 0.0).
- Values that exceed the meaningful display range (`> clip-max`) are also coerced to empty. This handles tool-state artifacts and out-of-band values that would otherwise disturb the chart's auto-scale.
- The chart pins Z range to 0–15 regardless of the post-clip data range, so values above 15 saturate to white per the Schlumberger field-print convention. The clip step is mainly about removing garbage.

**Output filename convention:** `TestData_FORGE_<channel>_clipped.txt`. The chart loader reads files matching this pattern by name (constants `AWCN_FILE` and `TTCN_FILE` in `MainWindow.xaml.cs`).

The clip filter prints a summary to stderr — kept-value range, null count, percent nulls — so you can verify the data looks sensible before committing.

---

## Step 3 (optional) — Visual sanity check

If you adapt this pipeline to a new dataset, it's worth visually verifying the clipped output before importing it into the chart. matplotlib's `imshow` is the simplest way — about ten lines of Python:

```python
import numpy as np, matplotlib.pyplot as plt

# Reshape the flat TSV into a 2D grid; nDepths × 180 azimuths
data = np.loadtxt("TestData_FORGE_AWCN_clipped.txt", delimiter="\t",
                  encoding="utf-16", usecols=2, comments=None)
grid = data.reshape(-1, 180)

plt.imshow(grid, aspect="auto", cmap="afmhot", vmin=0, vmax=15,
           origin="upper", interpolation="nearest")
plt.colorbar(label="normalized")
plt.savefig("preview.png", dpi=150)
```

If the preview doesn't show the expected sinusoidal banding (TTCN) or breakout streaks (AWCN), something upstream needs attention before the chart will look right.

---

## Adapting to a different dataset

If you want to use this chart with a different UBI well:

1. **Download or locate the source DLIS** for your target well's UBI run.
2. **Identify the right frame** — UBI tools typically expose multiple frames (LF, HF, scalars). The dual-contour chart wants the **HF pass with AWCN + TTCN channels**. Use `dlisio` to enumerate frames if unsure.
3. **Update the depth window** in `extract_azimuthal.py` arguments to match your well's open-hole interval.
4. **Verify channel naming** — `AWCN` and `TTCN` are Schlumberger-standard names but vendor logs may rename them. `extract_azimuthal.py --list` (if supported in your script version) will dump available channels.
5. **Adjust clip ranges** if your tool calibration differs from the FORGE dataset's. AWCN clip-max of 30 and TTCN clip-max of 50 are dataset-specific; check the field-print legend for your well's display range.
6. **Update repo-root constants** in `MainWindow.xaml.cs` if your data has different shape — particularly if your data isn't the 180-azimuth convention. The chart's `N_AZ_PER_CHANNEL = 180` and the synthesized azimuth array `1, 3, ..., 359` are convention-coded, not data-coded.

---

## Citation

If you use the FORGE 16A(78)-32 dataset in published work:

> McLennan, J. (2021). *Utah FORGE: Well 16A(78)-32 Logging Data — Run 3B*.
> Utah FORGE; University of Utah. DOI: [10.15121/1814488](https://doi.org/10.15121/1814488).
> CC-BY 4.0.

The FORGE program is a U.S. Department of Energy initiative for enhanced geothermal systems research. Public dataset access is provided via the Geothermal Data Repository at [https://gdr.openei.org](https://gdr.openei.org).
