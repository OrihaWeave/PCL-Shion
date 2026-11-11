using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Pages.Download.Comp;

namespace PCL.Shion.Test;

[TestClass]
public sealed class SearchTextTest
{
    [TestMethod]
    [DataRow("  Sodium  ", "Sodium")]
    [DataRow("钠 | Sodium", "Sodium")]
    [DataRow("Sodium | 钠", "Sodium")]
    [DataRow("Just Enough Items|JEI", "Just Enough Items|JEI")]
    [DataRow("钠", "钠")]
    [DataRow(null, "")]
    public void KeepsTheEnglishPartOfBilingualNames(string? input, string expected)
    {
        Assert.AreEqual(expected, PageComp.NormalizeSearchText(input));
    }
}
