"""Loads an App Watcher events.xes file and draws its directly-follows graph with PM4Py.

Optional. Needs:  pip install pm4py   (and Graphviz on PATH for the picture)

Usage:
  python scripts/pm4py_example.py <path-to>/events.xes [--out dfg.png]

Prints the directly-follows edges with their frequencies, and writes a picture when Graphviz is available.
"""
import argparse
import sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("xes", help="events.xes from an App Watcher export")
    ap.add_argument("--out", default="dfg.png", help="picture to write (needs Graphviz)")
    args = ap.parse_args()
    try:
        import pm4py
    except ImportError:
        print("pm4py is not installed. Run: pip install pm4py")
        return 1

    log = pm4py.read_xes(args.xes)
    print(f"traces: {log['case:concept:name'].nunique()}  events: {len(log)}")
    dfg, start, end = pm4py.discover_dfg(log)
    print("\nDirectly-follows edges (from -> to : count):")
    for (a, b), n in sorted(dfg.items(), key=lambda kv: -kv[1]):
        print(f"  {a} -> {b} : {n}")
    print("\nStart activities:", dict(start))
    print("End activities:", dict(end))
    try:
        pm4py.save_vis_dfg(dfg, start, end, args.out)
        print(f"\nPicture written to {args.out}")
    except Exception as exc:  # Graphviz missing is the usual cause
        print(f"\n(no picture: {exc})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
