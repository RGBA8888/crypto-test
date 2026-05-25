namespace Crypto.Domain.Pnl;

public sealed class WeightedAverageCostState
{
    public decimal Quantity { get; private set; }
    public decimal CostBasisUsd { get; private set; }
    public decimal RealizedPnlUsd { get; private set; }

    public void Buy(decimal qty, decimal costUsd)
    {
        if (qty <= 0 || costUsd < 0) return;
        Quantity += qty;
        CostBasisUsd += costUsd;
    }

    public void Sell(decimal qty, decimal proceedsUsd)
    {
        if (qty <= 0 || proceedsUsd < 0) return;

        if (Quantity <= 0)
        {
            // No known inventory; treat cost basis as 0 (diagnostics will flag incompleteness via transfers).
            RealizedPnlUsd += proceedsUsd;
            return;
        }

        var sellQty = Math.Min(qty, Quantity);
        var avgCost = CostBasisUsd / Quantity;
        var costOfSold = avgCost * sellQty;

        Quantity -= sellQty;
        CostBasisUsd -= costOfSold;
        RealizedPnlUsd += proceedsUsd - costOfSold;

        // If qty > Quantity (oversell), remaining portion treated as zero-basis.
        if (qty > sellQty)
        {
            RealizedPnlUsd += proceedsUsd * ((qty - sellQty) / qty);
        }
    }
}
