#!/usr/bin/env python3
"""
clip_null_filter.py — re-emit a UBI azimuthal TestData file with
out-of-range values coerced to null.

Input  : UTF-16-LE TSV with optional BOM, columns (azim_idx, depth_ft, value).
Output : same format. Empty value column = null.
Filter : value < --min OR value > --max  ->  null. Existing nulls preserved.
"""
import argparse, sys

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("output")
    ap.add_argument("--min", type=float, required=True)
    ap.add_argument("--max", type=float, required=True)
    args = ap.parse_args()

    edges  = [-100.0, 0.0, 5.0, 10.0, 15.0, 30.0, 50.0, float("inf")]
    labels = ["<-100", "-100..0", "0..5", "5..10", "10..15", "15..30", "30..50", ">=50"]
    def bucket(v):
        for i, e in enumerate(edges):
            if v < e:
                return i
        return len(edges) - 1

    n_rows = 0
    n_null_in = n_null_out = 0
    n_clip_low = n_clip_high = 0
    hist_in  = [0] * len(edges)
    hist_out = [0] * len(edges)
    vmin, vmax, vsum, vn = float("inf"), float("-inf"), 0.0, 0

    with open(args.input, "r", encoding="utf-16", newline="") as fin, \
         open(args.output, "w", encoding="utf-16", newline="") as fout:
        for raw in fin:
            n_rows += 1
            line = raw.rstrip("\r\n")
            parts = line.split("\t")
            if len(parts) < 3:
                fout.write(line + "\r\n")
                continue
            azim, depth, value = parts[0], parts[1], parts[2]
            if value == "":
                n_null_in += 1
                n_null_out += 1
                fout.write(f"{azim}\t{depth}\t\r\n")
                continue
            try:
                v = float(value)
            except ValueError:
                n_null_out += 1
                fout.write(f"{azim}\t{depth}\t\r\n")
                continue
            hist_in[bucket(v)] += 1
            if v < args.min:
                n_clip_low += 1
                n_null_out += 1
                fout.write(f"{azim}\t{depth}\t\r\n")
            elif v > args.max:
                n_clip_high += 1
                n_null_out += 1
                fout.write(f"{azim}\t{depth}\t\r\n")
            else:
                hist_out[bucket(v)] += 1
                if v < vmin: vmin = v
                if v > vmax: vmax = v
                vsum += v; vn += 1
                fout.write(f"{azim}\t{depth}\t{value}\r\n")

    out = sys.stderr.write
    out(f"\n=== {args.input} ===\n")
    out(f"clip range: [{args.min}, {args.max}]\n")
    out(f"rows total      : {n_rows}\n")
    out(f"null in (orig)  : {n_null_in}  ({100*n_null_in/n_rows:.2f}%)\n")
    out(f"clipped low (<{args.min}) : {n_clip_low}  ({100*n_clip_low/n_rows:.2f}%)\n")
    out(f"clipped high(>{args.max}) : {n_clip_high}  ({100*n_clip_high/n_rows:.2f}%)\n")
    out(f"null out (total): {n_null_out}  ({100*n_null_out/n_rows:.2f}%)\n")
    if vn:
        out(f"kept value range: min={vmin:.4f}  max={vmax:.4f}  mean={vsum/vn:.4f}  n={vn}\n")
    out("\nbucket distribution (BEFORE clip / AFTER clip):\n")
    for i, lab in enumerate(labels):
        before = hist_in[i]
        after  = hist_out[i]
        out(f"  {lab:>9} : before {before:>8}  after {after:>8}\n")
    out("\n")

if __name__ == "__main__":
    main()
