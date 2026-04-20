import csv
import os

# --- CONFIGURATION ---
LOG_PATH = "../backtest/Log.csv"
OUTPUT_PATH = "feature_matrix.csv"

def harvest_data():
    print(f"--- HARVESTING ALL FEATURES FROM {LOG_PATH} ---")
    if not os.path.exists(LOG_PATH):
        print("Error: Log.csv not found.")
        return

    bar_flow = {}    
    bar_struct = {}  
    signals = {}     
    outcomes = []    
    all_keys = set() 

    try:
        with open(LOG_PATH, 'r', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for row in reader:
                tag = row.get('Tag','').strip()
                bar = row.get('Bar','')
                cid = row.get('ConditionSetId','')
                ts = row.get('Timestamp','')
                detail = row.get('Detail','')

                if tag == 'FLOW':
                    ctx = {}
                    parts = detail.replace('|', '').split()
                    for p in parts:
                        if '=' in p:
                            try:
                                k, v = p.split('=')
                                key = f"f_{k.lower()}"
                                ctx[key] = float(v.replace('+', ''))
                                all_keys.add(key)
                            except: pass
                    bar_flow[bar] = ctx

                elif tag == 'STRUCT':
                    ctx = {}
                    parts = detail.replace('|', '').split()
                    for p in parts:
                        if '=' in p:
                            try:
                                k, v = p.split('=')
                                if '(' in v: v = v.split('(')[0]
                                key = f"s_{k.lower()}"
                                ctx[key] = float(v)
                                all_keys.add(key)
                            except: pass
                    bar_struct[bar] = ctx

                elif tag == 'EVAL':
                    sig_data = {
                        'raw_score': row.get('RawScore', '0'),
                        'eval_dir': row.get('Direction', ''),
                        'filter_reason': row.get('FilterReason', '')
                    }
                    for k, v in row.items():
                        kl = k.lower()
                        if kl.startswith('f_') or kl.startswith('s_') or kl.startswith('v_'):
                            try:
                                sig_data[kl] = float(v)
                                all_keys.add(kl)
                            except: sig_data[kl] = v
                    
                    signals[(bar, cid)] = sig_data

                elif tag == 'TOUCH_OUTCOME':
                    gate = row.get('GateReason','')
                    try:
                        orig_bar = gate.split(':')[-1]
                        sim_pnl = 0; mae = 0
                        
                        clean = detail.replace('|', ' ').replace(',', ' ')
                        pairs = clean.split()
                        for p in pairs:
                            if '=' in p:
                                k, v = p.split('=')
                                if k == 'PNL_CONS': sim_pnl = float(v)
                                elif k == 'MAE': mae = float(v)
                        
                        outcomes.append({
                            'bar': orig_bar, 'ts': ts, 'cid': cid, 'pnl': sim_pnl, 'hit': row.get('Label', ''), 'mae': mae, 'gate': gate
                        })
                    except Exception as e: pass
    except Exception as e:
        print(f"Error: {e}")
        return

    header = ['ts', 'cid', 'bar', 'pnl', 'hit', 'mae', 'gate', 'raw_score', 'eval_dir', 'filter_reason'] + sorted(list(all_keys))

    with open(OUTPUT_PATH, 'w', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=header)
        writer.writeheader()
        for o in outcomes:
            row_data = o.copy()
            if o['bar'] in bar_flow: row_data.update(bar_flow[o['bar']])
            if o['bar'] in bar_struct: row_data.update(bar_struct[o['bar']])
            if (o['bar'], o['cid']) in signals: row_data.update(signals[(o['bar'], o['cid'])])
            writer.writerow({k: row_data.get(k, '') for k in header})

    print(f"Created matrix with {len(outcomes)} outcomes.")

if __name__ == "__main__":
    harvest_data()