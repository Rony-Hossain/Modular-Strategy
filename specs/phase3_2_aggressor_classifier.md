# Phase 3.2 — Aggressor Classifier (Lee-Ready)

Upgrade TapeRecorder's naive `price≥ask→Buy` classification to a proper Lee-Ready algorithm with pre-tick BBO tracking and uptick/downtick fallback for mid-spread trades. Phase 3.1's naive rule misclassifies 20–50% of ticks at the midpoint; downstream detectors (BigPrint, sweep, velocity) need accurate sides.

## Files
- **Modified**: `ModularStrategy/TapeRecorder.cs` (add BBO tracking, Lee-Ready logic)
- **Modified**: `ModularStrategy/HostStrategy.cs` (forward Bid/Ask events to `_tape.OnBbo`)

**Do not touch**: `FootprintCore.cs`, any Phase 2 detector, `ConfluenceEngine.cs`. This is tape-internal only.

## Design

### Pre-tick BBO tracking

NT8 delivers `OnMarketData` events in sequence on the instrument thread. Bid/Ask events arrive interleaved with Last events. By tracking the most recent Bid/Ask *before* each Last, we get the true "pre-trade" BBO — the quote that was live when the trade executed.

New method on TapeRecorder:
```csharp
public void OnBbo(double bid, double ask);
```

Stores `_preBid` and `_preAsk` internally. These are the BBO that `OnTick` will use for classification, replacing the caller-supplied `bid/ask` fallback.

### Lee-Ready classification (replaces naive rule)

In `OnTick`, after resolving the BBO:

```
1. bid = _preBid if set, else caller-supplied bid
   ask = _preAsk if set, else caller-supplied ask
2. If price >= ask → Buy   (lifted the offer)
3. If price <= bid → Sell   (hit the bid)
4. Else (mid-spread):
   a. If price > _lastTradePrice → Buy   (uptick rule)
   b. If price < _lastTradePrice → Sell   (downtick rule)
   c. If price == _lastTradePrice → _lastSide   (zero-tick: carry forward)
5. Store _lastTradePrice = price, _lastSide = side
```

The Lee-Ready tick test is the standard academic approach (Lee & Ready 1991). Step 4c handles the zero-tick case by carrying forward — this is correct because a trade at the same price as the previous trade is most likely from the same aggressor.

### State fields added to TapeRecorder

```csharp
private double    _preBid;          // last bid seen via OnBbo
private double    _preAsk;          // last ask seen via OnBbo
private bool      _bboValid;        // at least one OnBbo call received this session
private double    _lastTradePrice;  // for uptick/downtick rule
private Aggressor _lastSide;        // for zero-tick carry-forward
```

All reset in `OnSessionOpen`.

## Host Wiring Change

In `HostStrategy.OnMarketData`, expand to forward Bid/Ask:

```csharp
protected override void OnMarketData(MarketDataEventArgs e)
{
    if (_tape == null) return;
    if (e.MarketDataType == MarketDataType.Bid || e.MarketDataType == MarketDataType.Ask)
    {
        _tape.OnBbo(GetCurrentBid(), GetCurrentAsk());
        return;
    }
    if (e.MarketDataType != MarketDataType.Last) return;
    _tape.OnTick(e.Time, e.Price, e.Volume, GetCurrentBid(), GetCurrentAsk());
}
```

Note: we call `GetCurrentBid/Ask()` on Bid/Ask events because by the time the event fires, the NT8 L1 state has already been updated with the new value. So `GetCurrentBid()` after a Bid event reflects the new bid.

## Invariants
- Zero allocation. No new objects, no boxing.
- `_preBid`/`_preAsk` are best-effort. If no Bid/Ask event arrives before the first Last (e.g., replay mode with sparse L1), `_bboValid` stays false and we fall back to caller-supplied bid/ask (same as Phase 3.1 behavior).
- `_lastTradePrice` and `_lastSide` reset to 0.0 and Unknown on session open. The first tick of the session uses the basic bid/ask rule only (no uptick/downtick possible without a prior trade).
- Lee-Ready is applied ONLY for mid-spread ticks. Ticks at or beyond the spread edges always use the quote test (steps 2–3).
- `OnBbo` is called on EVERY Bid and Ask event, not just changes. This is correct — we want the freshest BBO before the next Last.

## Validation
- [ ] Project builds, no warnings.
- [ ] Backtest results identical to Phase 3.1 baseline ($23,570) — classifier doesn't affect scoring.
- [ ] Debug print: `Tick.Side` distribution on a 30-second NQ window should show Unknown < 5% (down from ~20% with naive rule). If Unknown is still >10%, BBO tracking timing is off.
- [ ] Spot-check 20 mid-spread ticks in a replay: verify uptick/downtick classification matches visual tape direction.

## Do Not
- Add any scoring, veto, or ConfluenceEngine changes. This is tape-internal.
- Subscribe to `MarketDataType.DailyBar` or other event types.
- Buffer Bid/Ask events separately — just track the latest values.
- Use `DateTime` comparisons for BBO staleness. BBO is always "current" within the NT8 event stream.
