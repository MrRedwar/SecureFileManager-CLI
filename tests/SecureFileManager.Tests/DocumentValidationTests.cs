using System.IO;
using System.Net;
using System.Net.Sockets;
using SecureFileManager.Core.Files;

namespace SecureFileManager.Tests;

public sealed class DocumentValidationTests
{
    [Theory]
    [InlineData(DocumentFormat.Txt, "", true)]
    [InlineData(DocumentFormat.Txt, "Привет\r\nмир", true)]
    [InlineData(DocumentFormat.Json, "{\"name\":\"Египет\",\"a\":[1,true,null]}", true)]
    [InlineData(DocumentFormat.Json, "null", true)]
    [InlineData(DocumentFormat.Json, "", false)]
    [InlineData(DocumentFormat.Json, " ", false)]
    [InlineData(DocumentFormat.Json, "{\"x\":1,}", false)]
    [InlineData(DocumentFormat.Json, "{/*comment*/}", false)]
    [InlineData(DocumentFormat.Json, "{\"x\":1 \"y\":2}", false)]
    [InlineData(DocumentFormat.Xml, "<root>Русский текст &amp; данные</root>", true)]
    [InlineData(DocumentFormat.Xml, "<root><![CDATA[<!DOCTYPE sample>]]></root>", true)]
    [InlineData(DocumentFormat.Xml, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><root/>", true)]
    [InlineData(DocumentFormat.Xml, "", false)]
    [InlineData(DocumentFormat.Xml, "<root>", false)]
    [InlineData(DocumentFormat.Xml, "<a/><b/>", false)]
    [InlineData(DocumentFormat.Xml, "<!DOCTYPE root><root/>", false)]
    [InlineData(DocumentFormat.Xml, "<?xml version=\"1.0\" encoding=\"utf-16\"?><root/>", false)]
    public void StrictSyntaxAndEmptyDocuments(DocumentFormat format, string text, bool valid)
        => Assert.Equal(valid, DocumentValidator.Validate(format, text).IsValid);

    [Fact]
    public void ErrorsHavePositionsAndNeverEchoDocumentFragments()
    {
        foreach (var pair in new[] { (DocumentFormat.Json, "{\n\"PRIVATE_VALUE\":\n}"), (DocumentFormat.Xml, "<root>\n<PRIVATE_VALUE></root>") })
        {
            var result = DocumentValidator.Validate(pair.Item1, pair.Item2);
            Assert.False(result.IsValid); Assert.True(result.Line > 0); Assert.True(result.Position > 0);
            Assert.DoesNotContain("PRIVATE_VALUE", result.Description);
        }
    }
    [Fact]
    public void DepthNodeAttributeByteAndUnicodeLimitsAreBounded()
    {
        Assert.False(DocumentValidator.Validate(DocumentFormat.Json, new string('[', 65) + "0" + new string(']', 65)).IsValid);
        Assert.False(DocumentValidator.Validate(DocumentFormat.Xml, string.Concat(Enumerable.Repeat("<a>", 66)) + string.Concat(Enumerable.Repeat("</a>", 66))).IsValid);
        Assert.Equal("XML_COMPLEXITY", DocumentValidator.Validate(DocumentFormat.Xml, "<r>" + string.Concat(Enumerable.Repeat("<a/>", DocumentValidator.MaxXmlNodes)) + "</r>").Code);
        Assert.Equal("XML_COMPLEXITY", DocumentValidator.Validate(DocumentFormat.Xml, "<r " + string.Join(" ", Enumerable.Range(0, 1025).Select(i => $"a{i}=\"x\"")) + "/>").Code);
        Assert.Equal("SIZE", DocumentValidator.Validate(DocumentFormat.Txt, new string('я', FileRules.TextLimit / 2 + 1)).Code);
        Assert.Equal("ENCODING", DocumentValidator.Validate(DocumentFormat.Txt, "\uD800").Code);
        Assert.Equal("ENCODING", DocumentValidator.Validate(DocumentFormat.Txt, "\0").Code);
    }
    [Fact]
    public void ExternalEntitiesCannotReadExclusivelyLockedLocalFileOrConnectToLoopback()
    {
        var path = Path.Combine(Path.GetTempPath(), "sfm-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, "PRIVATE_SENTINEL");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            foreach (var uri in new[] { new Uri(path).AbsoluteUri, $"http://127.0.0.1:{port}/external.dtd" })
                foreach (var text in new[] { $"<!DOCTYPE r [<!ENTITY x SYSTEM '{uri}'>]><r>&x;</r>", $"<!DOCTYPE r SYSTEM '{uri}'><r/>", $"<broken><!DOCTYPE r SYSTEM '{uri}'>" })
                {
                    var result = DocumentValidator.Validate(DocumentFormat.Xml, text);
                    Assert.Equal("XML_DTD", result.Code); Assert.True(result.Prohibited);
                    Assert.DoesNotContain("PRIVATE_SENTINEL", result.Description);
                    Assert.False(listener.Pending(), "XML attempted a forbidden network connection.");
                    Assert.Equal(0, locked.Position); // validator did not acquire/read the locked file
                }
        }
        finally { File.Delete(path); }
    }
}
