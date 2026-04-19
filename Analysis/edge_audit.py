import csv
import os
from collections import defaultdict

def run_edge_audit():
    input_path = "feature_matrix.csv"
    if not os.path.exists(input_path):
        print("Error: feature_matrix.csv not found.")
        return

    data = []
    with open(input_path, 'r') as f:
        reader = csv.DictReader(f)
        for row in reader:
            for k, v in row.items():
                if v and k not in ['ts', 'cid', 'hit', 'gate', 'eval_dir']:
                    try: row[k] = float(v)
                    except: pass
            data.append(row)

    by_cid = defaultdict(list)
    for row in data: by_cid[row['cid']].append(row)

    print(f"\n{'SIGNAL':<20} | {'COUNT':>5} | {'BASELINE EV':>10} | {'WIN%'}")
    print("-" * 50)

    for cid, signals in by_cid.items():
        pnls = [s['pnl'] for s in signals if isinstance(s['pnl'], (int, float))]
        if not pnls: continue
        ev = sum(pnls) / len(pnls)
        wr = (len([p for p in pnls if p > 0]) / len(pnls)) * 100
        print(f"{cid:<20} | {len(signals):>5} | ${ev:>9.0f} | {wr:>5.1f}%")

if __name__ == "__main__":
    run_edge_audit()
