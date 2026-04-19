import csv
import os

# --- CONFIGURATION ---
LOG_PATH = "../backtest/Log.csv"
OUTPUT_PATH = "feature_matrix.csv"

def harvest_data():
    print(f"--- HARVESTING SIGNAL DATA FROM {LOG_PATH} ---")
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
                                key = f"flow_{k}"
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
                                key = f"struct_{k}"
                                ctx[key] = float(v)
                                all_keys.add(key)
                            except: pass
                    bar_struct[bar] = ctx

                elif tag == 'EVAL':
                    signals[(bar, cid)] = {
                        'eval_score': row.get('Score', '0'),
                        'eval_dir': row.get('Direction', '')
                    }
                    all_keys.add('eval_score'); all_keys.add('eval_dir')

                elif tag == 'TOUCH_OUTCOME':
                    gate = row.get('GateReason','')
                    try:
                        orig_bar = gate.split(':')[-1]
                        sim_pnl = 0; hit = ''; mae = 0
                        for part in detail.split():
                            if part.startswith('SIM_PNL='): sim_pnl = part.split('=')[1].replace('$','').replace(',','')
                            elif part.startswith('FIRST_HIT='): hit = part.split('=')[1]
                            elif part.startswith('MAE='): mae = part.split('=')[1].replace('$','').replace(',','')
                        
                        outcomes.append({
                            'bar': orig_bar, 'ts': ts, 'cid': cid, 'pnl': sim_pnl, 'hit': hit, 'mae': mae, 'gate': gate
                        })
                    except: pass
    except Exception as e:
        print(f"Error parsing: {e}")
        return

    header = ['ts', 'cid', 'bar', 'pnl', 'hit', 'mae', 'gate'] + sorted(list(all_keys))

    with open(OUTPUT_PATH, 'w', newline='') as f:
        writer = csv.DictWriter(f, fieldnames=header)
        writer.writeheader()
        for o in outcomes:
            row_data = o.copy()
            if o['bar'] in bar_flow: row_data.update(bar_flow[o['bar']])
            if o['bar'] in bar_struct: row_data.update(bar_struct[o['bar']])
            if (o['bar'], o['cid']) in signals: row_data.update(signals[(o['bar'], o['cid'])])
            writer.writerow({k: row_data.get(k, '') for k in header})

    print(f"Created feature matrix: {OUTPUT_PATH}")

if __name__ == "__main__":
    harvest_data()
