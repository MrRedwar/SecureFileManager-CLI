using System.Text;
using System.Text.Json;
using System.Xml;

namespace SecureFileManager.Core.Files;

public enum DocumentFormat { Txt, Json, Xml }
public sealed record DocumentValidation(bool IsValid, string Code, string Message, long? Line = null,
    long? Position = null, bool Prohibited = false)
{
    public string Description => Message + (Line is null ? "" : $" Строка {Line}, позиция {Position}.");
    public static DocumentValidation Valid { get; } = new(true, "VALID", "Документ корректен.");
}

// Pure, bounded parsers. No resolver, arbitrary type activation, or filesystem operations.
public static class DocumentValidator
{
    public const int MaxDepth = 64;
    public const int MaxXmlNodes = 200000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static DocumentFormat Format(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".txt" => DocumentFormat.Txt, ".json" => DocumentFormat.Json, ".xml" => DocumentFormat.Xml,
        _ => throw new FileCommandException(FileStatus.Unsupported)
    };
    public static bool Supports(string name) => new[] { ".txt", ".json", ".xml" }
        .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static DocumentValidation Validate(DocumentFormat format, string text)
    {
        if (text.Length > FileRules.TextLimit) return new(false, "SIZE", "Документ превышает 10 МиБ.");
        try
        {
            if (Utf8.GetByteCount(text) > FileRules.TextLimit) return new(false, "SIZE", "Документ превышает 10 МиБ UTF-8.");
        }
        catch (EncoderFallbackException) { return new(false, "ENCODING", "Текст содержит недопустимую последовательность Unicode."); }
        if (text.Contains('\0')) return new(false, "ENCODING", "Нулевые символы в текстовом документе не поддерживаются.");
        if (format == DocumentFormat.Txt) return DocumentValidation.Valid;
        if (string.IsNullOrWhiteSpace(text)) return new(false, "EMPTY", "Документ пуст: требуется " + format.ToString().ToUpperInvariant() + ".", 1, 1);
        if (format == DocumentFormat.Json)
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions
                { MaxDepth = MaxDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
                return DocumentValidation.Valid;
            }
            catch (JsonException ex)
            {
                // Raw parser messages may contain property values, paths or document fragments.
                return new(false, "JSON_SYNTAX", "Ошибка JSON: проверьте скобки, запятые, кавычки и вложенность (до 64). Позиция указана в байтах UTF-8.",
                    ex.LineNumber + 1, ex.BytePositionInLine + 1);
            }
        }
        try
        {
            using var input = new StringReader(text);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = FileRules.TextLimit, MaxCharactersFromEntities = 1024,
                ConformanceLevel = ConformanceLevel.Document, CheckCharacters = true
            });
            var nodes = 0;
            while (reader.Read())
            {
                if (++nodes > MaxXmlNodes || reader.Depth > MaxDepth || reader.AttributeCount > 1024)
                    return new(false, "XML_COMPLEXITY", "Превышен лимит сложности XML: глубина 64, 200000 узлов, 1024 атрибута на элемент.");
                if (reader.NodeType == XmlNodeType.XmlDeclaration && reader.GetAttribute("encoding") is { } encoding &&
                    !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                    return new(false, "XML_ENCODING", "Поддерживается только XML с кодировкой UTF-8.", Prohibited: true);
            }
            return DocumentValidation.Valid;
        }
        catch (XmlException ex)
        {
            // Classification only: security is enforced by the XmlReader settings above.
            // A declaration inside valid CDATA/comment text must remain ordinary text.
            if (text.Contains("<!DOCTYPE", StringComparison.Ordinal) || text.Contains("<!ENTITY", StringComparison.Ordinal))
                return new(false, "XML_DTD", "DTD и объявления сущностей XML запрещены.", ex.LineNumber, ex.LinePosition, true);
            return new(false, "XML_SYNTAX", "Ошибка XML: проверьте корневой элемент, имена, закрывающие теги и атрибуты.", ex.LineNumber, ex.LinePosition);
        }
    }
}
