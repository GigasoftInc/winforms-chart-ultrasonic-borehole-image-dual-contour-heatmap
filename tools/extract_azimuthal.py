#!/usr/bin/env python3
"""
extract_azimuthal.py — Pull a [36]-dim azimuthal channel from a USIT DLIS
and write it as a ProEssentials TestData_FORGE_*.txt file.

Sibling of extract_vdl.py. Same TestData on-disk format (UTF-16 LE BOM,
tab-separated, CRLF, columns: X, depth, Z). For VDL X is time in µs;
for the cement-map track X is azimuth in degrees (10° sectors).

Designed for FORGE 16A(78)-32 R3A USIT Main DLIS. Defaults match what
the Schlumberger Casing Integrity Evaluation report (10-Dec-2020,
pages 12–27) actually displays:

    AWBK   First Echo Amplitude minus average  [dB]    hot palette
    THBK   Thickness minus average              [in]    divergent palette
    IRBK   Internal Radii minus average         [in]    divergent palette
    AIBK   Acoustic Impedance                   [Mrayl] hot palette  (cement-quality)

Usage:
    # Enumerate frames and channels:
    python extract_azimuthal.py R3A_USIT_Main.dlis --list

    # Extract AWBK at full depth resolution:
    python extract_azimuthal.py R3A_USIT_Main.dlis \
        --channel AWBK --output TestData_FORGE_AWBK.txt --preview AWBK_preview.png

    # Extract THBK with a depth window:
    python extract_azimuthal.py R3A_USIT_Main.dlis \
        --channel THBK --depth-range 800 4500 \
        --output TestData_FORGE_THBK.txt --preview THBK_preview.png
"""

from __future__ import annotations
import argparse
import codecs
import sys
from pathlib import Path

import numpy as np
from dlisio import dlis
from dlisio.common import ErrorHandler, Actions


# --------------------------------------------------------------------------- #
# DLIS loading — permissive by default. FORGE files are commonly truncated    #
# at the final logical record and dlisio's strict default raises on that.     #
# --------------------------------------------------------------------------- #

def load_permissive(path: str):
    """Open a DLIS, tolerating truncation and minor structural issues."""
    handler = ErrorHandler(
        critical=Actions.LOG_ERROR,    # don't raise on truncation
        major=Actions.LOG_ERROR,
        minor=Actions.LOG_ERROR,
    )
    return dlis.load(path, error_handler=handler)


# --------------------------------------------------------------------------- #
# --list mode — enumerate frames and channels                                 #
# --------------------------------------------------------------------------- #

def list_channels(path: str) -> None:
    files = load_permissive(path)
    for f in files:
        print(f"=== Logical file: {f.fileheader.id} ===")
        for fr in f.frames:
            print(f"\n  Frame {fr.name}  index={fr.index_type}  "
                  f"channels={len(fr.channels)}")
            for ch in fr.channels:
                dims = "x".join(str(d) for d in ch.dimension) if ch.dimension else "scalar"
                units = ch.units or ""
                lname = ch.long_name or ""
                print(f"    {ch.name:24s}  [{dims:>6s}]  {units:<10s}  {lname}")


# --------------------------------------------------------------------------- #
# Core extract                                                                #
# --------------------------------------------------------------------------- #

def _depth_curve(fr):
    """Find the depth index curve in a frame."""
    depth_ch = next(
        (c for c in fr.channels
         if c.name.upper() in ("TDEP", "DEPT", "DEPTH", "MD")),
        None,
    )
    return depth_ch if depth_ch is not None else fr.channels[0]


def find_channel(files, channel_name: str, frame_filter: str | None = None):
    """Return (frame, channel, depth_curve) for a named channel.

    A USIT DLIS commonly has the same channel in multiple frames (a short
    calibration / repeat pass and the main pass). Picking the FIRST match
    yields the wrong frame ~half the time. We collect every match and
    pick the one with the most depth samples — that's the main pass.

    --frame can pin to a specific frame name (e.g. '30B') when the
    biggest-frame heuristic isn't what you want.
    """
    matches = []  # list of (frame, channel, depth_curve, n_depths)
    for f in files:
        for fr in f.frames:
            if frame_filter and fr.name.upper() != frame_filter.upper():
                continue
            for ch in fr.channels:
                if ch.name.upper() == channel_name.upper():
                    depth_ch = _depth_curve(fr)
                    try:
                        n = len(depth_ch.curves())
                    except Exception:
                        n = 0
                    matches.append((fr, ch, depth_ch, n))

    if not matches:
        if frame_filter:
            raise SystemExit(
                f"channel {channel_name!r} not found in frame {frame_filter!r}"
            )
        raise SystemExit(f"channel {channel_name!r} not found in any frame")

    # Largest frame wins.
    matches.sort(key=lambda m: m[3], reverse=True)
    fr, ch, depth_ch, n = matches[0]

    if len(matches) > 1:
        print(f"  {channel_name.upper()} found in {len(matches)} frames:")
        for f2, _, _, n2 in matches:
            mark = "  <-- selected" if f2.name == fr.name else ""
            print(f"    Frame {f2.name:<8s}  {n2:>7,} depth samples{mark}")
    else:
        print(f"  Frame: {fr.name}   ({n:,} depth samples)")

    return fr, ch, depth_ch


def extract_array(path: str, channel_name: str,
                  depth_range: tuple[float, float] | None,
                  depth_stride: int,
                  frame_filter: str | None = None
                  ) -> tuple[np.ndarray, np.ndarray, np.ndarray, str]:
    """Return (depths_ft, azimuths_deg, values_2d, units)."""
    files = load_permissive(path)
    frame, ch, depth_ch = find_channel(files, channel_name, frame_filter)

    # Read the data for the entire frame (cheap — one frame fits in memory easily)
    depths = np.asarray(depth_ch.curves(), dtype=np.float64)
    values = np.asarray(ch.curves())   # shape: (n_depths, 36)

    # dlisio reports depth in the unit declared by the channel.
    # USIT depth is normally 0.1 inch; convert to feet.
    dunit = (depth_ch.units or "").strip().lower()
    if dunit in ("0.1 in", "0.1in"):
        depths_ft = depths * 0.1 / 12.0
    elif dunit in ("in", "inch"):
        depths_ft = depths / 12.0
    elif dunit in ("ft", "feet"):
        depths_ft = depths
    elif dunit in ("m", "meter", "metre"):
        depths_ft = depths * 3.28084
    else:
        # Heuristic: USIT depths in 0.1 in are O(50000-500000), in ft are O(0-10000)
        if depths.max() > 50000:
            depths_ft = depths * 0.1 / 12.0
        else:
            depths_ft = depths.astype(np.float64)

    # Sanity: ensure increasing depth (DLIS may store reverse)
    if depths_ft[0] > depths_ft[-1]:
        depths_ft = depths_ft[::-1]
        values = values[::-1, :]

    # Apply depth range filter
    if depth_range:
        lo, hi = depth_range
        mask = (depths_ft >= lo) & (depths_ft <= hi)
        depths_ft = depths_ft[mask]
        values = values[mask, :]
        if depths_ft.size == 0:
            raise SystemExit(f"depth range [{lo}, {hi}] yields zero rows")

    # Apply stride
    if depth_stride > 1:
        depths_ft = depths_ft[::depth_stride]
        values = values[::depth_stride, :]

    # Azimuth axis: 36 sectors, centered at 5°, 15°, ..., 355°
    n_az = values.shape[1]
    azimuths_deg = (np.arange(n_az) + 0.5) * (360.0 / n_az)

    return depths_ft.astype(np.float32), azimuths_deg.astype(np.float32), \
           values.astype(np.float32), (ch.units or "")


# --------------------------------------------------------------------------- #
# TestData writer — matches the format MainWindow.xaml.cs reads               #
#   UTF-16 LE with BOM, tab-separated, CRLF                                   #
#   col 0: X (azimuth °) — empty when X == 0                                  #
#   col 1: depth (ft)                                                         #
#   col 2: value — empty when null/NaN                                        #
#   Order: outer = depth, inner = X                                           #
# --------------------------------------------------------------------------- #

def write_testdata(path: str, x_axis: np.ndarray, depths: np.ndarray,
                   z: np.ndarray) -> None:
    out = Path(path)
    with out.open("wb") as fh:
        fh.write(codecs.BOM_UTF16_LE)
        # Build in chunks for speed; ~6000 × 36 = 216k lines is trivial.
        lines: list[str] = []
        for i, d in enumerate(depths):
            for j, x in enumerate(x_axis):
                xstr = "" if x == 0.0 else f"{x:g}"
                v = z[i, j]
                vstr = "" if (np.isnan(v) or v == -999.25) else f"{v:.4f}"
                lines.append(f"{xstr}\t{d:g}\t{vstr}\r\n")
        fh.write("".join(lines).encode("utf-16-le"))
    print(f"  wrote {out}  ({out.stat().st_size:,} bytes, "
          f"{len(depths):,} depths × {len(x_axis)} azimuths)")


# --------------------------------------------------------------------------- #
# Preview rendering — matplotlib, with palettes that match the                #
# Schlumberger CSL report (10-Dec-2020, pages 12–27).                         #
# --------------------------------------------------------------------------- #

# Palette spec per channel — keep in one place so the preview matches what
# the report actually displays and what the PE chart will eventually use.
PALETTES = {
    # Hot ramp, single-sided. Background-corrected echo amplitude reads
    # high (bright) over good cement, low (dark) over channels. The CSL
    # report uses warm yellows/oranges with a darker low end.
    "AWBK": dict(cmap="afmhot",   vmin=-1.4, vmax=4.1, label="dB"),

    # Diverging, signed. Below-average thickness (loss) on one side,
    # above-average on the other. CSL report uses blue/orange diverging.
    "THBK": dict(cmap="RdBu_r",   vmin=-0.04, vmax=0.02, label="in"),
    "IRBK": dict(cmap="RdBu_r",   vmin=-0.04, vmax=0.02, label="in"),

    # AIBK is the cement-impedance channel — keep for completeness even
    # though the current target is the casing-integrity report's tracks.
    "AIBK": dict(cmap="hot",      vmin=0.0,  vmax=8.0, label="Mrayl"),
}


def render_preview(out_path: str, channel: str,
                   depths_ft: np.ndarray, azimuths_deg: np.ndarray,
                   values: np.ndarray, units: str) -> None:
    import matplotlib.pyplot as plt

    pal = PALETTES.get(channel.upper(), dict(cmap="viridis", vmin=None,
                                             vmax=None, label=units))

    fig, ax = plt.subplots(figsize=(4, 11), dpi=120)
    im = ax.imshow(
        values,
        aspect="auto",
        origin="upper",                   # depth increases downward
        extent=(azimuths_deg.min(), azimuths_deg.max(),
                depths_ft.max(), depths_ft.min()),
        cmap=pal["cmap"], vmin=pal["vmin"], vmax=pal["vmax"],
        interpolation="nearest",
    )
    ax.set_title(f"FORGE 16A(78)-32  {channel.upper()}")
    ax.set_xlabel("Azimuth (°)")
    ax.set_ylabel("Depth (ft)")
    ax.set_xticks([0, 90, 180, 270, 360])
    cbar = fig.colorbar(im, ax=ax, shrink=0.7, pad=0.02)
    cbar.set_label(pal["label"] or units)
    fig.tight_layout()
    fig.savefig(out_path, bbox_inches="tight")
    plt.close(fig)
    print(f"  preview → {out_path}")


# --------------------------------------------------------------------------- #
# CLI                                                                         #
# --------------------------------------------------------------------------- #

def main() -> None:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("dlis_path")
    p.add_argument("--list", action="store_true",
                   help="enumerate frames and channels, then exit")
    p.add_argument("--channel", default="AWBK",
                   help="channel name to extract (default: AWBK)")
    p.add_argument("--output", default=None,
                   help="output TestData file (default: TestData_FORGE_<CHANNEL>.txt)")
    p.add_argument("--preview", default=None,
                   help="optional matplotlib preview PNG")
    p.add_argument("--frame", default=None,
                   help="restrict to a specific frame name (e.g. 30B). "
                        "Default: auto-pick the frame with the most depth samples.")
    p.add_argument("--depth-range", type=float, nargs=2, metavar=("MIN", "MAX"),
                   default=None, help="depth window in feet (e.g. 800 4500)")
    p.add_argument("--depth-stride", type=int, default=1,
                   help="keep every Nth depth (default 1 = full resolution)")
    args = p.parse_args()

    if args.list:
        list_channels(args.dlis_path)
        return

    out = args.output or f"TestData_FORGE_{args.channel.upper()}.txt"

    print(f"Extracting {args.channel.upper()} from {args.dlis_path}")
    depths, az, values, units = extract_array(
        args.dlis_path,
        channel_name=args.channel,
        depth_range=tuple(args.depth_range) if args.depth_range else None,
        depth_stride=args.depth_stride,
        frame_filter=args.frame,
    )

    n_valid = np.isfinite(values).sum()
    print(f"  shape: {values.shape}   units: {units!r}   "
          f"valid cells: {n_valid:,} / {values.size:,}")
    print(f"  depth: {depths[0]:.2f} → {depths[-1]:.2f} ft   "
          f"value range: [{np.nanmin(values):.4f}, {np.nanmax(values):.4f}]")

    write_testdata(out, az, depths, values)

    if args.preview:
        render_preview(args.preview, args.channel, depths, az, values, units)


if __name__ == "__main__":
    main()
