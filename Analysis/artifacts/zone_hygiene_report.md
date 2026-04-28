# Zone Hygiene Audit Report

Audit conducted on 2026-04-25 05:14:17

**Configuration:**
- `TICK_SIZE`: 0.25
- `POINT_VALUE`: 5.0
- `MIN_SAMPLE_SIZE_OVERALL`: 30
- `MIN_SAMPLE_SIZE_SUBGROUP`: 50
- `MIN_SAMPLE_SIZE_C_SHARP`: 100

## Audit Findings

### 2. Zone Lifecycle & Stale Zone Detection

Zone lifecycle data is unavailable or `TICK_SIZE` is zero. Cannot perform stale zone detection.

### 3. SR Zone Broken Events Audit

No `SR_ZONE_BROKEN WARN` events found in `Log.csv`.

### 4. Zone Mitigation Effectiveness

No `ZONE_MITIGATED` events or zone lifecycle data available. Cannot audit mitigation effectiveness.

### 5. Zone Interaction with Signals and Trades

Signal or zone lifecycle data is unavailable, or `TICK_SIZE` is zero. Cannot perform zone interaction analysis.

### 6. Unaccounted for Zones (Orphaned Zones)

Zone lifecycle data is unavailable. Cannot detect orphaned zones.

