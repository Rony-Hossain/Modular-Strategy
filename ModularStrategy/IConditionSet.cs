#region Using declarations
using MathLogic.Strategy;
using MarketSnapshot = MathLogic.Strategy.MarketSnapshot;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// THE ATOM OF THE PLUG-AND-PLAY ARCHITECTURE.
    ///
    /// A condition set is exactly one thing: a named bundle of market conditions
    /// that produces a trade decision or nothing.
    ///
    /// Rules for implementors:
    ///   - Return RawDecision.None if conditions are not met. Never throw.
    ///   - Set IsValid = true only when ALL entry conditions are satisfied.
    ///   - Set Source, Direction, EntryPrice, StopPrice, TargetPrice, Label, RawScore.
    ///   - Do NOT manage state that persists across sessions — reset in OnSessionOpen().
    ///   - Do NOT reference NT8 APIs directly — read everything from MarketSnapshot.
    ///   - Do NOT size positions — that is SignalGenerator's job.
    ///
    /// To add a new strategy:
    ///   1. Create one .cs file implementing this interface.
    ///   2. Register it in HostStrategy: new StrategyEngine(..., new YourSet()).
    ///   3. Done. The engine, gates, orders, and logging all work automatically.
    /// </summary>
    public interface IConditionSet
    {
        /// <summary>
        /// Unique identifier for this condition set.
        /// Format: "Name_vN" e.g. "ORB_Classic_v1", "SMF_Impulse_v1".
        /// Appears in every CSV log row and on the NT8 chart entry label.
        /// Use this to filter and rank strategies in post-trade analysis.
        /// </summary>
        string SetId { get; }

        /// <summary>
        /// Called once when the strategy initialises.
        /// Use to set up any state, register indicator keys, etc.
        /// </summary>
        void Initialise(double tickSize, double tickValue);

        /// <summary>
        /// Called on every new session open. Reset intraday state here.
        /// </summary>
        void OnSessionOpen(MarketSnapshot snapshot);

        /// <summary>
        /// Called on every bar. Return a valid RawDecision when all conditions
        /// are met, or RawDecision.None when they are not.
        /// </summary>
        RawDecision Evaluate(MarketSnapshot snapshot);

        /// <summary>
        /// Called when an entry fill is confirmed for a signal this set produced.
        /// Use to suppress re-entry or update internal state.
        /// </summary>
        void OnFill(SignalObject signal, double fillPrice);

        /// <summary>
        /// Called when a position opened by this set is closed.
        /// </summary>
        void OnClose(SignalObject signal, double exitPrice, double pnl);

        /// <summary>
        /// Diagnostic hook: returns the reason why the last Evaluate() call returned RawDecision.None.
        /// </summary>
        string LastDiagnostic { get; }
    }
}
