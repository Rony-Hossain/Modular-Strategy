import pandas as pd
import numpy as np
import pathlib
import re
import pyarrow.parquet as pq # Required for pandas to_parquet/read_parquet with 'pyarrow' engine

# Set numpy random seed for reproducibility
np.random.seed(42)

# Define paths using pathlib for robustness
BASE_DIR = pathlib.Path(__file__).parent.parent.parent
LOG_CSV_PATH = BASE_DIR / "backtest" / "Log.csv"
SCHEMA_MD_PATH = BASE_DIR / "Analysis" / "artifacts" / "schema.md"
ZONE_LIFECYCLE_PARQUET_PATH = BASE_DIR / "Analysis" / "artifacts" / "zone_lifecycle.parquet"
SIGNALS_PARQUET_PATH = BASE_DIR / "Analysis" / "artifacts" / "signals.parquet"
STRATEGY_CONFIG_PY_PATH = BASE_DIR / "strategy_config.py"

OUTPUT_REPORT_PATH = BASE_DIR / "Analysis" / "artifacts" / "25_zone_hygiene_report.md"
OUTPUT_BUGS_PARQUET_PATH = BASE_DIR / "Analysis" / "artifacts" / "25_zone_hygiene_bugs.parquet"

# --- Helper Functions ---
def parse_strategy_config(file_path):
    """
    Parses strategy configuration file for relevant zone-related parameters.
    """
    config = {}
    try:
        with open(file_path, 'r') as f:
            content = f.read()
        
        # Example: looking for a specific stale zone duration in minutes
        match = re.search(r'STALE_ZONE_DURATION_MINUTES\s*=\s*(\d+)', content)
        if match:
            config['STALE_ZONE_DURATION_MINUTES'] = int(match.group(1))
        
        # Add other config parsing as needed (e.g., SR_LEVEL_TOLERANCE)
        match_sr_tolerance = re.search(r'SR_LEVEL_TOLERANCE\s*=\s*([0-9.]+)', content)
        if match_sr_tolerance:
            config['SR_LEVEL_TOLERANCE'] = float(match_sr_tolerance.group(1))

    except FileNotFoundError:
        print(f"[ERROR] Strategy config file not found at {file_path}")
    except Exception as e:
        print(f"[ERROR] Error parsing strategy config file: {e}")
    return config

def extract_zone_id_from_message(message):
    """
    Extracts ZoneId from log messages.
    """
    if pd.isna(message):
        return None
    # Regex to capture ZoneId (alphanumeric string) following "Zone" or "SR Zone"
    match = re.search(r'(?:Zone|SR Zone)\s*(\w+)', str(message))
    if match:
        return match.group(1)
    return None

def impute_log_timestamps(df_log_input):
    """
    Imputes '0001-01-01 00:00:00' Timestamps for ZONE_MITIGATED/SR_ZONE_BROKEN events
    within each Bar group. Carries forward the timestamp of the last preceding valid event
    within the same Bar, or the Bar's timestamp itself if available.
    """
    df_log = df_log_input.copy()
    
    df_log['Timestamp'] = pd.to_datetime(df_log['Timestamp'])
    df_log['BarTimestamp'] = pd.to_datetime(df_log['BarTimestamp'])

    # Define tags for which timestamp imputation is required
    imputation_tags = ['ZONE_MITIGATED', 'SR_ZONE_BROKEN']

    # Create a temporary column for sorting.
    # We want valid timestamps to sort before '0001-01-01' markers, so NaT is used
    df_log['__sort_key'] = df_log['Timestamp']
    df_log.loc[df_log['Timestamp'] == pd.Timestamp.min, '__sort_key'] = pd.NaT

    # Add an original index to ensure stable sort for tie-breaking
    df_log['__original_index'] = df_log.index

    # Sort within each bar: valid timestamps first, then NaT (for '0001-01-01'), then by original index
    df_log_sorted = df_log.sort_values(by=['Bar', '__sort_key', '__original_index'])

    # Create a temporary column to hold potentially imputed timestamps
    df_log_sorted['__temp_imputed_ts'] = df_log_sorted['Timestamp']
    
    # Mark timestamps that need imputation as NaT in the temporary column
    needs_imputation_mask = (df_log_sorted['Tag'].isin(imputation_tags)) & \
                            (df_log_sorted['Timestamp'] == pd.Timestamp.min)
    df_log_sorted.loc[needs_imputation_mask, '__temp_imputed_ts'] = pd.NaT

    # Perform forward fill on the temporary imputed timestamps within each bar.
    # This fills NaT entries with the last *valid* timestamp within the same bar.
    df_log_sorted['__temp_imputed_ts'] = df_log_sorted.groupby('Bar')['__temp_imputed_ts'].ffill()

    # For any remaining NaT values (meaning '0001-01-01' events were at the beginning of their Bar
    # or had no prior valid timestamp to fill from), use the BarTimestamp.
    df_log_sorted['Timestamp'] = df_log_sorted['__temp_imputed_ts'].fillna(df_log_sorted['BarTimestamp'])

    # Revert to original index order and drop temporary columns
    df_log_final = df_log_sorted.set_index('__original_index').sort_index().drop(columns=['__sort_key', '__temp_imputed_ts'])

    return df_log_final

# --- Main Script ---
def main():
    print("[INPUT] Loading data...")

    # 1. Parse Data
    try:
        df_log = pd.read_csv(LOG_CSV_PATH)
        print(f"[INPUT] Loaded Log.csv with {len(df_log)} records.")
    except FileNotFoundError:
        print(f"[ERROR] Log.csv not found at {LOG_CSV_PATH}")
        return
    except Exception as e:
        print(f"[ERROR] Error loading Log.csv: {e}")
        return
    
    try:
        df_zone_lifecycle = pd.read_parquet(ZONE_LIFECYCLE_PARQUET_PATH)
        print(f"[INPUT] Loaded zone_lifecycle.parquet with {len(df_zone_lifecycle)} records.")
    except FileNotFoundError:
        print(f"[ERROR] zone_lifecycle.parquet not found at {ZONE_LIFECYCLE_PARQUET_PATH}")
        return
    except Exception as e:
        print(f"[ERROR] Error loading zone_lifecycle.parquet: {e}")
        return
    
    try:
        df_signals = pd.read_parquet(SIGNALS_PARQUET_PATH)
        print(f"[INPUT] Loaded signals.parquet with {len(df_signals)} records.")
    except FileNotFoundError:
        # Create an empty DataFrame if file not found to allow script to continue
        df_signals = pd.DataFrame(columns=['Timestamp', 'SignalId', 'Symbol', 'Side', 'Price', 'Size', 'Type', 'EntryId', 'ZoneId'])
        print("[ANOMALY] signals.parquet not found, proceeding with empty signals DataFrame.")
    except Exception as e:
        print(f"[ERROR] Error loading signals.parquet: {e}")
        df_signals = pd.DataFrame(columns=['Timestamp', 'SignalId', 'Symbol', 'Side', 'Price', 'Size', 'Type', 'EntryId', 'ZoneId'])
        print("[ANOMALY] Error loading signals.parquet, proceeding with empty signals DataFrame.")

    # Load schema.md (content is not parsed, just fulfilling requirement to load)
    try:
        with open(SCHEMA_MD_PATH, 'r') as f:
            _ = f.read()
        print(f"[INPUT] Loaded schema.md.")
    except FileNotFoundError:
        print(f"[ERROR] schema.md not found at {SCHEMA_MD_PATH}")
        print("[ANOMALY] schema.md not found, proceeding assuming standard schema.")
    except Exception as e:
        print(f"[ERROR] Error loading schema.md: {e}")
        print("[ANOMALY] Error loading schema.md, proceeding assuming standard schema.")
    
    strategy_config = parse_strategy_config(STRATEGY_CONFIG_PY_PATH)
    print(f"[INPUT] Parsed strategy_config.py: {strategy_config}")

    print("[PARSE] Processing dataframes...")

    # Filter Log.csv for relevant tags and impute Timestamps
    relevant_log_tags = ['SIGNAL_ACCEPTED', 'SIGNAL_REJECTED', 'ZONE_MITIGATED', 'SR_ZONE_BROKEN', 'TA_DECISION']
    df_log_filtered = df_log[df_log['Tag'].isin(relevant_log_tags)].copy()
    
    if df_log_filtered.empty:
        print("[WARNING] No relevant log events found for analysis. Exiting.")
        with open(OUTPUT_REPORT_PATH, 'w') as f:
            f.write("# Zone Hygiene Report\n\nNo relevant log events found for analysis.")
        # Ensure an empty parquet file with expected schema is created
        pd.DataFrame(columns=['BugType', 'Description', 'Timestamp', 'ZoneId', 'ExpectedStateBefore', 'ActualStateBefore', 'SourceFile', 'LogTag', 'Message']).to_parquet(OUTPUT_BUGS_PARQUET_PATH)
        return

    df_log_processed = impute_log_timestamps(df_log_filtered)
    df_log_processed['ZoneId_from_Log'] = df_log_processed['Message'].apply(extract_zone_id_from_message)
    print(f"[PARSE] Log.csv timestamps imputed and ZoneIds extracted. Total {len(df_log_processed)} relevant records.")

    # Ensure timestamps for zone_lifecycle and signals are datetime objects
    df_zone_lifecycle['Timestamp'] = pd.to_datetime(df_zone_lifecycle['Timestamp'])
    df_signals['Timestamp'] = pd.to_datetime(df_signals['Timestamp'])


    print("[CHECK] Starting Zone State Reconstruction & Consistency Check...")

    # Prepare zone_lifecycle events with flags for state changes
    df_zone_lifecycle_events = df_zone_lifecycle.copy()
    
    # Create a combined event timeline by concatenating events from different sources
    # 1. Zone lifecycle events (source of truth for zone state)
    timeline_df_lifecycle = df_zone_lifecycle_events[['Timestamp', 'ZoneId', 'EventType']].rename(columns={'EventType': 'LifecycleEventType'})
    timeline_df_lifecycle['Source'] = 'ZoneLifecycle'
    timeline_df_lifecycle['LogTag'] = None
    timeline_df_lifecycle['Message'] = None # No message from lifecycle events directly

    # 2. Log events that directly report a zone state change (for cross-verification)
    log_zone_change_events = df_log_processed[df_log_processed['Tag'].isin(['ZONE_MITIGATED', 'SR_ZONE_BROKEN'])].copy()
    timeline_df_log_reports = log_zone_change_events[['Timestamp', 'ZoneId_from_Log', 'Tag', 'Message']].rename(columns={'ZoneId_from_Log': 'ZoneId', 'Tag': 'LogTag'})
    timeline_df_log_reports = timeline_df_log_reports[timeline_df_log_reports['ZoneId'].notna()] # Only include if ZoneId was parsed
    timeline_df_log_reports['Source'] = 'LogReport'
    timeline_df_log_reports['LifecycleEventType'] = None

    # 3. General log events (e.g., Signal ACCEPTED/REJECTED for checks)
    general_log_events_df = df_log_processed[~df_log_processed['Tag'].isin(['ZONE_MITIGATED', 'SR_ZONE_BROKEN'])].copy()
    timeline_df_general_log = general_log_events_df[['Timestamp', 'ZoneId_from_Log', 'Tag', 'Message']].rename(columns={'ZoneId_from_Log': 'ZoneId', 'Tag': 'LogTag'})
    timeline_df_general_log['Source'] = 'GeneralLog'
    timeline_df_general_log['LifecycleEventType'] = None

    # Concatenate all event sources and sort them chronologically
    full_timeline_df = pd.concat([timeline_df_lifecycle, timeline_df_log_reports, timeline_df_general_log], ignore_index=True)
    full_timeline_df = full_timeline_df.sort_values(by=['Timestamp']).reset_index(drop=True)

    bugs_list = []
    # Global state tracking for zones
    zone_active_status = {}         # ZoneId -> bool (True if active, False if inactive)
    zone_last_lifecycle_event = {}  # ZoneId -> (EventType, Timestamp) - tracks what lifecycle event last occurred
    zone_creation_timestamps = {}   # ZoneId -> Timestamp - first creation/activation time
    active_zone_start_timestamps = {} # ZoneId -> Timestamp - when zone became *currently* active

    for idx, row in full_timeline_df.iterrows():
        current_timestamp = row['Timestamp']
        zone_id = row['ZoneId'] # This ZoneId can be None for GeneralLog events not mentioning a zone
        source = row['Source']
        log_tag = row['LogTag']
        message = row['Message']

        # --- Process ZoneLifecycle events (source of truth for zone state) ---
        if source == 'ZoneLifecycle':
            lifecycle_event_type = row['LifecycleEventType']
            
            # Update creation timestamps (first time we see a zone created/activated)
            if lifecycle_event_type in ['CREATED', 'ACTIVATED']:
                zone_creation_timestamps.setdefault(zone_id, current_timestamp)
                active_zone_start_timestamps[zone_id] = current_timestamp # Mark as currently active

            prev_active_status = zone_active_status.get(zone_id, False) # Default to inactive if zone never seen
            prev_lifecycle_event_type, prev_lifecycle_event_ts = zone_last_lifecycle_event.get(zone_id, (None, None))

            # Bug: Zone activated but was already active
            if lifecycle_event_type in ['CREATED', 'ACTIVATED'] and prev_active_status:
                bugs_list.append({
                    'BugType': 'ZONE_STATE_INCONSISTENCY',
                    'Description': f"Zone {zone_id} ({lifecycle_event_type}) at {current_timestamp} but was already active according to zone_lifecycle (last event: {prev_lifecycle_event_type} at {prev_lifecycle_event_ts}).",
                    'Timestamp': current_timestamp,
                    'ZoneId': zone_id,
                    'ExpectedStateBefore': 'Inactive',
                    'ActualStateBefore': 'Active',
                    'SourceFile': 'zone_lifecycle.parquet',
                    'LogTag': None, 'Message': None
                })
            
            # Bug: Zone mitigated/broken but was not active
            if lifecycle_event_type in ['MITIGATED', 'BROKEN'] and not prev_active_status:
                bugs_list.append({
                    'BugType': 'ZONE_STATE_INCONSISTENCY',
                    'Description': f"Zone {zone_id} ({lifecycle_event_type}) at {current_timestamp} but was not active according to zone_lifecycle (last event: {prev_lifecycle_event_type} at {prev_lifecycle_event_ts}).",
                    'Timestamp': current_timestamp,
                    'ZoneId': zone_id,
                    'ExpectedStateBefore': 'Active',
                    'ActualStateBefore': 'Inactive',
                    'SourceFile': 'zone_lifecycle.parquet',
                    'LogTag': None, 'Message': None
                })
            
            # Update the current active status based on the lifecycle event
            if lifecycle_event_type in ['CREATED', 'ACTIVATED']:
                zone_active_status[zone_id] = True
            elif lifecycle_event_type in ['MITIGATED', 'BROKEN']:
                zone_active_status[zone_id] = False
                active_zone_start_timestamps.pop(zone_id, None) # Remove from currently active tracking
            
            zone_last_lifecycle_event[zone_id] = (lifecycle_event_type, current_timestamp)

        # --- Process Log Reports (cross-verification against zone_lifecycle) ---
        elif source == 'LogReport' and pd.notna(zone_id):
            expected_lifecycle_event = 'MITIGATED' if log_tag == 'ZONE_MITIGATED' else 'BROKEN'
            time_tolerance = pd.Timedelta(seconds=1) # Allow for minor timestamp differences

            # Check LOG_LIFECYCLE_MISMATCH: Log reports a state change, but zone_lifecycle doesn't
            matching_lifecycle_event = df_zone_lifecycle_events[
                (df_zone_lifecycle_events['ZoneId'] == zone_id) &
                (df_zone_lifecycle_events['EventType'] == expected_lifecycle_event) &
                (df_zone_lifecycle_events['Timestamp'] >= current_timestamp - time_tolerance) &
                (df_zone_lifecycle_events['Timestamp'] <= current_timestamp + time_tolerance)
            ]
            
            if matching_lifecycle_event.empty:
                bugs_list.append({
                    'BugType': 'LOG_LIFECYCLE_MISMATCH',
                    'Description': f"Log reports '{log_tag}' for Zone {zone_id} at {current_timestamp}, but no corresponding lifecycle event '{expected_lifecycle_event}' found in zone_lifecycle.parquet within {time_tolerance} window.",
                    'Timestamp': current_timestamp,
                    'ZoneId': zone_id,
                    'ExpectedStateBefore': 'Active (implied by log event)', # Log implies it was active before being mitigated/broken
                    'ActualStateBefore': zone_active_status.get(zone_id, False),
                    'SourceFile': 'Log.csv',
                    'LogTag': log_tag, 'Message': message
                })
            
            # Check ZONE_HYGIENE_REDUNDANT_EVENT: Log reports X, but zone was already inactive (based on lifecycle)
            if zone_id in zone_active_status and not zone_active_status[zone_id]:
                prev_event_type, prev_event_ts = zone_last_lifecycle_event.get(zone_id, (None, None))
                bugs_list.append({
                    'BugType': 'ZONE_HYGIENE_REDUNDANT_EVENT',
                    'Description': f"Log reports '{log_tag}' for Zone {zone_id} at {current_timestamp}, but zone was already inactive (last lifecycle event: {prev_event_type} at {prev_event_ts}).",
                    'Timestamp': current_timestamp,
                    'ZoneId': zone_id,
                    'ExpectedStateBefore': 'Active',
                    'ActualStateBefore': 'Inactive',
                    'SourceFile': 'Log.csv',
                    'LogTag': log_tag, 'Message': message
                })

            # Check ZONE_HYGIENE_PREMATURE_EVENT: Log reports X, but zone was never created/activated
            if zone_id not in zone_creation_timestamps:
                 bugs_list.append({
                    'BugType': 'ZONE_HYGIENE_PREMATURE_EVENT',
                    'Description': f"Log reports '{log_tag}' for Zone {zone_id} at {current_timestamp}, but zone was never created or activated according to zone_lifecycle.",
                    'Timestamp': current_timestamp,
                    'ZoneId': zone_id,
                    'ExpectedStateBefore': 'Created/Activated',
                    'ActualStateBefore': 'Never Seen',
                    'SourceFile': 'Log.csv',
                    'LogTag': log_tag, 'Message': message
                })

        # --- Process General Log Events (e.g., Signal checks) ---
        elif source == 'GeneralLog':
            current_active_zones_snapshot = {z_id for z_id, is_active in zone_active_status.items() if is_active}

            if log_tag in ['SIGNAL_ACCEPTED', 'SIGNAL_REJECTED'] and not df_signals.empty:
                signal_id_from_log = None
                if message:
                    signal_id_match = re.search(r'SignalId:\s*(\w+)', message)
                    if signal_id_match:
                        signal_id_from_log = signal_id_match.group(1)

                matching_signals = pd.DataFrame()
                if signal_id_from_log:
                    matching_signals = df_signals[df_signals['SignalId'] == signal_id_from_log]
                
                # If no SignalId in log message or no match, try proximity by timestamp
                if matching_signals.empty:
                    matching_signals = df_signals[
                        (df_signals['Timestamp'] >= current_timestamp - pd.Timedelta(seconds=5)) &
                        (df_signals['Timestamp'] <= current_timestamp + pd.Timedelta(seconds=5))
                    ]
                
                if not matching_signals.empty:
                    signal = matching_signals.iloc[0] # Take the first matching signal
                    signal_zone_id = signal.get('ZoneId') # Signals might have an associated ZoneId

                    # Rule: If a signal is explicitly associated with a zone, that zone should be active for signal generation/acceptance.
                    if pd.notna(signal_zone_id) and signal_zone_id not in current_active_zones_snapshot:
                        bugs_list.append({
                            'BugType': 'SIGNAL_ZONE_MISMATCH',
                            'Description': f"Log reports '{log_tag}' for Signal {signal.get('SignalId', 'N/A')} at {current_timestamp}. Signal associated with Zone {signal_zone_id}, but that zone was inactive.",
                            'Timestamp': current_timestamp,
                            'ZoneId': signal_zone_id,
                            'ExpectedStateBefore': 'Active',
                            'ActualStateBefore': 'Inactive',
                            'SourceFile': 'signals.parquet / Log.csv',
                            'LogTag': log_tag, 'Message': message
                        })
                    
                    # Rule: A signal rejected due to 'Zone violated' should indeed be within an inactive zone.
                    if log_tag == 'SIGNAL_REJECTED' and "Zone violated" in str(message):
                        # If the signal is rejected due to zone violation, but its associated zone is active, it's a bug.
                        if pd.notna(signal_zone_id) and signal_zone_id in current_active_zones_snapshot:
                             bugs_list.append({
                                'BugType': 'SIGNAL_REJECTED_MISMATCH',
                                'Description': f"Signal {signal.get('SignalId', 'N/A')} rejected for 'Zone violated' at {current_timestamp} but associated Zone {signal_zone_id} was active.",
                                'Timestamp': current_timestamp,
                                'ZoneId': signal_zone_id,
                                'ExpectedStateBefore': 'Inactive (for rejection)',
                                'ActualStateBefore': 'Active',
                                'SourceFile': 'signals.parquet / Log.csv',
                                'LogTag': log_tag, 'Message': message
                            })
                        # If message indicates zone violation but no associated zone or any active zone to justify rejection
                        # This check is harder without knowing actual zone boundaries. For now, focus on explicit zone_id.


    # Stale Zone Check (if STALE_ZONE_DURATION_MINUTES is configured)
    if 'STALE_ZONE_DURATION_MINUTES' in strategy_config and strategy_config['STALE_ZONE_DURATION_MINUTES'] > 0:
        stale_duration = pd.Timedelta(minutes=strategy_config['STALE_ZONE_DURATION_MINUTES'])
        # Iterate through zones that are still marked as active at the very end of the log
        last_timestamp_in_log = df_log_processed['Timestamp'].max()
        if pd.notna(last_timestamp_in_log):
            for zone_id, start_ts in active_zone_start_timestamps.items():
                if last_timestamp_in_log - start_ts > stale_duration:
                    bugs_list.append({
                        'BugType': 'ZONE_HYGIENE_STALE_ZONE',
                        'Description': f"Zone {zone_id} has been continuously active since {start_ts} and exceeded stale duration of {stale_duration}. It was still active at {last_timestamp_in_log}.",
                        'Timestamp': last_timestamp_in_log,
                        'ZoneId': zone_id,
                        'ExpectedStateBefore': 'Mitigated/Broken (due to staleness)',
                        'ActualStateBefore': 'Active',
                        'SourceFile': 'zone_lifecycle.parquet (implied absence of event)',
                        'LogTag': None, 'Message': None
                    })
            print(f"[CHECK] Completed stale zone check with duration: {stale_duration}")
    else:
        print("[CHECK] Skipping stale zone check: STALE_ZONE_DURATION_MINUTES not configured or set to 0.")

    df_bugs = pd.DataFrame(bugs_list)
    if df_bugs.empty:
        print("[RESULT] No bugs found related to zone hygiene and SR level integrity.")
        bug_summary = "No bugs found related to zone hygiene and SR level integrity."
    else:
        print(f"[RESULT] Found {len(df_bugs)} potential bugs.")
        bug_summary = f"Found {len(df_bugs)} potential bugs. See `25_zone_hygiene_bugs.parquet` for details."
        print("[ANOMALY] Bugs detected. Review the output parquet file.")

    # 4. Audit ZONE_MITIGATED & SR_ZONE_BROKEN Events
    print("\n[AUDIT] Auditing ZONE_MITIGATED and SR_ZONE_BROKEN events...")
    
    audit_report = [
        "# Zone Hygiene and SR Level Integrity Audit Report\n",
        bug_summary + "\n",
        "## Audit Findings\n"
    ]

    # Audit ZONE_MITIGATED events
    mitigated_events_log = df_log_processed[df_log_processed['Tag'] == 'ZONE_MITIGATED']
    mitigated_events_lifecycle = df_zone_lifecycle_events[df_zone_lifecycle_events['LifecycleEventType'] == 'MITIGATED']

    audit_report.append(f"### 1. ZONE_MITIGATED Events Overview\n")
    audit_report.append(f"Total `ZONE_MITIGATED` events reported in `Log.csv`: {len(mitigated_events_log)}\n")
    audit_report.append(f"Total `MITIGATED` events recorded in `zone_lifecycle.parquet`: {len(mitigated_events_lifecycle)}\n")

    if not mitigated_events_log.empty:
        mitigated_by_zone_log = mitigated_events_log['ZoneId_from_Log'].value_counts(dropna=False)
        audit_report.append(f"\n#### `ZONE_MITIGATED` counts by ZoneId (from Log.csv):\n")
        audit_report.append(mitigated_by_zone_log.to_markdown())
        audit_report.append("\n")
    else:
        audit_report.append("No `ZONE_MITIGATED` events found in `Log.csv`.\n")

    if not mitigated_events_lifecycle.empty:
        mitigated_by_zone_lifecycle = mitigated_events_lifecycle['ZoneId'].value_counts(dropna=False)
        audit_report.append(f"\n#### `MITIGATED` counts by ZoneId (from zone_lifecycle.parquet):\n")
        audit_report.append(mitigated_by_zone_lifecycle.to_markdown())
        audit_report.append("\n")
    else:
        audit_report.append("No `MITIGATED` events found in `zone_lifecycle.parquet`.\n")

    # Audit SR_ZONE_BROKEN events
    broken_events_log = df_log_processed[df_log_processed['Tag'] == 'SR_ZONE_BROKEN']
    broken_events_lifecycle = df_zone_lifecycle_events[df_zone_lifecycle_events['LifecycleEventType'] == 'BROKEN']

    audit_report.append(f"### 2. SR_ZONE_BROKEN Events Overview\n")
    audit_report.append(f"Total `SR_ZONE_BROKEN` events reported in `Log.csv`: {len(broken_events_log)}\n")
    audit_report.append(f"Total `BROKEN` events recorded in `zone_lifecycle.parquet`: {len(broken_events_lifecycle)}\n")

    if not broken_events_log.empty:
        broken_by_zone_log = broken_events_log['ZoneId_from_Log'].value_counts(dropna=False)
        audit_report.append(f"\n#### `SR_ZONE_BROKEN` counts by ZoneId (from Log.csv):\n")
        audit_report.append(broken_by_zone_log.to_markdown())
        audit_report.append("\n")
    else:
        audit_report.append("No `SR_ZONE_BROKEN` events found in `Log.csv`.\n")

    if not broken_events_lifecycle.empty:
        broken_by_zone_lifecycle = broken_events_lifecycle['ZoneId'].value_counts(dropna=False)
        audit_report.append(f"\n#### `BROKEN` counts by ZoneId (from zone_lifecycle.parquet):\n")
        audit_report.append(broken_by_zone_lifecycle.to_markdown())
        audit_report.append("\n")
    else:
        audit_report.append("No `BROKEN` events found in `zone_lifecycle.parquet`.\n")

    # Add detected bugs to the report
    audit_report.append(f"### 3. Detected Bugs & Inconsistencies\n")
    if not df_bugs.empty:
        bug_type_counts = df_bugs['BugType'].value_counts()
        audit_report.append("\n#### Bug Type Summary:\n")
        audit_report.append(bug_type_counts.to_markdown())
        audit_report.append("\n")
        
        audit_report.append("\n#### Sample of Bugs (first 5):\n")
        audit_report.append(df_bugs.head(5).to_markdown(index=False))
        audit_report.append("\n")
    else:
        audit_report.append("No specific bugs or inconsistencies were identified during this analysis.\n")


    print("[RESULT] Generating report and saving bugs...")
    
    # Save the report
    with open(OUTPUT_REPORT_PATH, 'w') as f:
        f.write("\n".join(audit_report))
    print(f"[SAVED] Report saved to {OUTPUT_REPORT_PATH}")

    # Define the expected schema for the bugs DataFrame
    bug_df_schema_columns = [
        'BugType', 'Description', 'Timestamp', 'ZoneId', 
        'ExpectedStateBefore', 'ActualStateBefore', 'SourceFile', 'LogTag', 'Message'
    ]
    
    if not df_bugs.empty:
        # Ensure 'Timestamp' is datetime before saving to parquet
        df_bugs['Timestamp'] = pd.to_datetime(df_bugs['Timestamp'])
        # Select and reorder columns to match schema if necessary
        df_bugs = df_bugs.reindex(columns=bug_df_schema_columns)
        df_bugs.to_parquet(OUTPUT_BUGS_PARQUET_PATH)
        print(f"[SAVED] Bugs data saved to {OUTPUT_BUGS_PARQUET_PATH}")
    else:
        # Create an empty parquet file with the defined schema if no bugs
        pd.DataFrame(columns=bug_df_schema_columns).to_parquet(OUTPUT_BUGS_PARQUET_PATH)
        print(f"[SAVED] Empty bugs data saved to {OUTPUT_BUGS_PARQUET_PATH} as no bugs were found.")


if __name__ == "__main__":
    main()