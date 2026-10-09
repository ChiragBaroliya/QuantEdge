using QuantEdge.Infrastructure.Services;
using Xunit;
using Existing = QuantEdge.Infrastructure.Services.InstrumentSyncService.ExistingInstrument;

namespace QuantEdge.Tests;

public class InstrumentReconcileTests
{
    private static readonly Dictionary<string, int> Zerodha = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TMPV"] = 884737,
        ["INFY"] = 408065,
        ["AAREYDRUGS"] = 1342721,
        ["HFCL-BE"] = 7777777,
        ["NIFTY 50"] = 256265
    };

    [Fact]
    public void ChangedTokenOfAListedSymbol_IsUpdated()
    {
        var result = InstrumentSyncService.ReconcileExisting(Zerodha, new[]
        {
            new Existing { Symbol = "INFY", InstrumentToken = 111, IsActive = true },
            new Existing { Symbol = "TMPV", InstrumentToken = 884737, IsActive = true } // unchanged
        });

        var change = Assert.Single(result.TokenChanges);
        Assert.Equal(new InstrumentSyncService.TokenChange("INFY", 111, 408065), change);
        Assert.Empty(result.MissingActive);
    }

    [Fact]
    public void ActiveSymbolNoLongerListed_IsReportedWithItsNewListing_NotChanged()
    {
        var result = InstrumentSyncService.ReconcileExisting(Zerodha, new[]
        {
            new Existing { Symbol = "HFCL", InstrumentToken = 5619457, IsActive = true },
            new Existing { Symbol = "AAREYDRUGS-BE", InstrumentToken = 1343489, IsActive = true },
            new Existing { Symbol = "GONECO", InstrumentToken = 42, IsActive = true }
        });

        Assert.Empty(result.TokenChanges);
        Assert.Collection(result.MissingActive,
            m => { Assert.Equal("HFCL", m.Symbol); Assert.Equal(new[] { "HFCL-BE" }, m.ListedAs); },
            m => { Assert.Equal("AAREYDRUGS-BE", m.Symbol); Assert.Equal(new[] { "AAREYDRUGS" }, m.ListedAs); },
            m => { Assert.Equal("GONECO", m.Symbol); Assert.Empty(m.ListedAs); });
    }

    [Fact]
    public void InactiveSymbolNoLongerListed_IsIgnored()
    {
        var result = InstrumentSyncService.ReconcileExisting(Zerodha, new[]
        {
            new Existing { Symbol = "TATAMOTORS", InstrumentToken = 884737, IsActive = false }
        });

        Assert.Empty(result.TokenChanges);
        Assert.Empty(result.MissingActive);
    }
}
