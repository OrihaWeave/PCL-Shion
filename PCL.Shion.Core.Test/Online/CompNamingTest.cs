using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Online;

namespace PCL.Shion.Core.Test;

[TestClass]
public sealed class CompNamingTest
{
    [TestMethod]
    [DataRow("sodium-0.6.jar", 0, "【钠】sodium-0.6.jar")]
    [DataRow("sodium-0.6.jar", 1, "[钠] sodium-0.6.jar")]
    [DataRow("sodium-0.6.jar", 2, "钠-sodium-0.6.jar")]
    [DataRow("sodium-0.6.jar", 3, "sodium-0.6-钠.jar")]
    [DataRow("README", 3, "README-钠")]
    [DataRow(".jar", 3, "钠.jar")]
    [DataRow(null, 3, "钠")]
    [DataRow("sodium.jar", 9, "sodium.jar")]
    public void PutsTheChineseNameBeforeTheExtension(string? fileName, int format, string expected)
    {
        Assert.AreEqual(expected, CompNaming.Combine(fileName, "钠", format));
    }
}
