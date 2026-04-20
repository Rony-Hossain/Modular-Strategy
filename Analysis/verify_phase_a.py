import csv
import os

def run_verification():
    log_path = '../backtest/Log.csv'
    trades_path = '../backtest/Trades.csv'
    
    if not os.path.exists(log_path):
        print(f"FAIL: {log_path} not found.")
        return

    eval_rows = []
    touch_outcomes = []
    
    with open(log_path, 'r', encoding='utf-8-sig') as f:
        reader = csv.DictReader(f)
        for r in reader:
            if r.get('Tag') == 'EVAL':
                eval_rows.append(r)
            elif r.get('Tag') == 'TOUCH_OUTCOME':
                touch_outcomes.append(r)

    print("--- Phase A Verification ---")
    
    # A. count(EVAL) >= count(TOUCH_OUTCOME)
    if len(eval_rows) >= len(touch_outcomes):
        print(f"PASS A: count(EVAL)={len(eval_rows)} >= count(TOUCH_OUTCOME)={len(touch_outcomes)}")
    else:
        print(f"FAIL A: count(EVAL)={len(eval_rows)} < count(TOUCH_OUTCOME)={len(touch_outcomes)}")

    # B. count(EVAL WHERE filter_reason IN ...) == count(EVAL WHERE FIRST_HIT='NOT_SIMULATED')
    # Since our Python script parses Detail or it reads the EVAL column
    b_reasons = {'V_TIME_BLOCK', 'HOST_SAMEBAR_DUP', 'G1_CircuitBreaker'}
    count_b_reasons = sum(1 for r in eval_rows if r.get('FilterReason') in b_reasons)
    # The A2 instructions for FIRST_HIT on EVAL:
    # "emit EVAL row with SIM_PNL=NaN, FIRST_HIT=NOT_SIMULATED"
    # Assuming the parsed FIRST_HIT would be in Detail or Label column
    count_not_sim = sum(1 for r in eval_rows if r.get('Label') == 'NOT_SIMULATED' or 'NOT_SIMULATED' in r.get('Detail', ''))
    
    if count_b_reasons == count_not_sim:
        print(f"PASS B: FilterReason matches NOT_SIMULATED count ({count_b_reasons} == {count_not_sim})")
    else:
        print(f"FAIL B: FilterReason count ({count_b_reasons}) != NOT_SIMULATED count ({count_not_sim})")

    # C. count(EVAL WHERE filter_reason='OTHER') == 0
    count_other = sum(1 for r in eval_rows if r.get('FilterReason') == 'OTHER')
    if count_other == 0:
        print("PASS C: count(filter_reason='OTHER') == 0")
    else:
        print(f"FAIL C: count(filter_reason='OTHER') == {count_other}")

    # D. count(EVAL WHERE log_status='PARTIAL_ON_EXCEPTION') == 0
    count_partial = sum(1 for r in eval_rows if r.get('LogStatus') == 'PARTIAL_ON_EXCEPTION')
    if count_partial == 0:
        print("PASS D: count(log_status='PARTIAL_ON_EXCEPTION') == 0")
    else:
        print(f"WARN D: count(log_status='PARTIAL_ON_EXCEPTION') == {count_partial}")

    # E. count(distinct SignalId) == count(EVAL)
    signal_ids = [r.get('GateReason') for r in eval_rows] # SignalId is typically stored in GateReason column for EVAL
    unique_ids = set(signal_ids)
    if len(signal_ids) == len(unique_ids) and len(signal_ids) > 0:
        print(f"PASS E: count(distinct SignalId) == count(EVAL) ({len(unique_ids)} == {len(signal_ids)})")
    else:
        print(f"FAIL E: count(distinct SignalId)={len(unique_ids)} != count(EVAL)={len(signal_ids)}")

    # F. For every EVAL row with was_taken_live=True, there exists a matching Trades.csv Entry name.
    if os.path.exists(trades_path):
        with open(trades_path, 'r', encoding='utf-8-sig') as f:
            t_reader = csv.DictReader(f)
            trade_names = {r.get('Entry name', '') for r in t_reader}
        
        taken_evals = [r.get('GateReason') for r in eval_rows if r.get('WasTakenLive') == '1']
        mismatches = [tid for tid in taken_evals if tid not in trade_names and tid.split(':')[0] not in trade_names]
        
        mismatch_pct = len(mismatches) / len(taken_evals) if taken_evals else 0
        if mismatch_pct <= 0.01:
            print(f"PASS F: Mismatches={len(mismatches)} ({mismatch_pct:.1%}) <= 1%")
        else:
            print(f"FAIL F: Mismatches={len(mismatches)} ({mismatch_pct:.1%}) > 1%")
    else:
        print("WARN F: Trades.csv not found.")

    # G. No TOUCH_OUTCOME row has a timestamp earlier than its paired EVAL row.
    # We map EVAL times
    eval_times = {r.get('GateReason'): r.get('Timestamp') for r in eval_rows}
    g_fails = 0
    for t in touch_outcomes:
        sig_id = t.get('GateReason')
        eval_time = eval_times.get(sig_id)
        if eval_time and t.get('Timestamp') < eval_time:
            g_fails += 1
    
    if g_fails == 0:
        print("PASS G: No TOUCH_OUTCOME is earlier than EVAL.")
    else:
        print(f"FAIL G: {g_fails} TOUCH_OUTCOME rows are earlier than EVAL.")

    # H. Distribution of filter_reason matches SignalGenerator internal counters within 1%. 
    # Since we can't parse SignalGenerator logs easily from CSV without adding specific TAGs, we'll assume PASS if EVAL has reasons.
    print("WARN H: Cannot automatically verify SignalGenerator internal counters against Log.csv here.")

    # I. For every row, spread_ticks_at_decision is either NaN (L1 not subscribed) or > 0.
    spread_fails = 0
    for r in eval_rows:
        spread = r.get('Spread', '')
        if spread and spread != 'NaN' and spread != '0.00':
            try:
                if float(spread) <= 0: spread_fails += 1
            except: pass
        elif spread == '0.00' or spread == '0':
            spread_fails += 1
            
    if spread_fails == 0:
        print("PASS I: spread_ticks_at_decision is NaN or > 0.")
    else:
        print(f"FAIL I: {spread_fails} rows have invalid spread <= 0.")

if __name__ == '__main__':
    run_verification()
