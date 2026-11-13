using System.Globalization;
using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>What a <c>PageWrite</c>'s step 4 refused (<see cref="PageWriteCheck.Validate"/>).</summary>
public enum PageWriteProblemKind
{
    /// <summary>The XML fails the 2013 schema gate (Q3); <see cref="PageWriteProblem.Schema"/> says where.</summary>
    InvalidXml,

    /// <summary>The root isn't a 2013 <c>one:Page</c>, or its <c>ID</c> isn't the page the call names (Q1).</summary>
    PageIdMismatch,

    /// <summary>Sent <c>objectID</c>s that step 3's read doesn't have (AD-7).</summary>
    UnknownObjectId,

    /// <summary>Sent <c>objectID</c>s on an element of another kind than the one step 3 read with that ID.</summary>
    WrongElement,

    /// <summary>Removal names that aren't an OE under a sent Outline (AD-7, Q1).</summary>
    RemovalNotAnOe,

    /// <summary>Removal names the call also sends.</summary>
    RemovalAlsoSent,

    /// <summary>OEs, or other elements with an <c>objectID</c> (a table, row or cell), sent under an Outline other than the one step 3 read them in (AD-7 as amended for 2.9 Q4).</summary>
    OeMoved,

    /// <summary>OEs step 3 read under a sent Outline that it left out and didn't name for removal (AD-7 whole Outlines).</summary>
    OeOmitted,
}

/// <summary>
/// One step-4 refusal: its kind and the <c>objectID</c>s it concerns (for <see cref="PageWriteProblemKind.OeOmitted"/>,
/// the omitted OEs), or the schema problems. The caller decides whether a message may name the IDs (AD-21).
/// </summary>
public sealed record PageWriteProblem(PageWriteProblemKind Kind, IReadOnlyList<string> ObjectIds, IReadOnlyList<SchemaProblem>? Schema = null);

/// <summary>
/// Step 4's outcome: the first problem found, or none; and the elements step 5 compares (AD-17 as amended for 2.9 Q5):
/// every sent <c>one:Outline</c> with an <c>objectID</c>, then every sent <c>one:OE</c> with an <c>objectID</c> outside
/// any sent Outline (in practice the title's OE), in document order.
/// </summary>
public sealed record PageWriteValidation(PageWriteProblem? Problem, IReadOnlyList<string> HashTargets);

/// <summary>What the <c>PageWrite</c> read-back found (AD-7).</summary>
public enum ReadBackDiffKind
{
    /// <summary>An attribute sent on <see cref="ReadBackDiff.ObjectId"/> that OneNote didn't apply.</summary>
    AttributeDiffers,

    /// <summary>An OE sent as <see cref="ReadBackDiff.ObjectId"/> that came back as <see cref="ReadBackDiff.NewObjectId"/>.</summary>
    ObjectIdChanged,

    /// <summary>A sent element with an <c>objectID</c> that isn't on the page after the write.</summary>
    Missing,

    /// <summary>A sent Outline whose OE lists came back with a different number of OEs at some level.</summary>
    OeCountDiffers,

    /// <summary>A sent Outline that came back with fewer images than were sent in it.</summary>
    ImageLost,
}

/// <summary>What a <c>delete_page_content</c> target is on step 3's read (<see cref="PageWriteCheck.DeleteTarget"/>).</summary>
public enum DeleteTargetKind
{
    /// <summary>A <c>one:Outline</c> on the page: the only target v1 deletes (AD-7).</summary>
    Outline,

    /// <summary>On the page, but not a <c>one:Outline</c>: an OE, the title's OE, an image, ink, a table and so on.</summary>
    NotAnOutline,

    /// <summary>No element on the page has that <c>objectID</c>.</summary>
    Unknown,
}

/// <summary>One read-back finding: IDs, attribute names and counts only, never content (AD-9).</summary>
public sealed record ReadBackDiff(
    ReadBackDiffKind Kind,
    string ObjectId,
    string? Attribute = null,
    string? NewObjectId = null,
    int SentImages = 0,
    int ImagesAfter = 0);

/// <summary>
/// The pure part of a <c>PageWrite</c> (AD-7, AD-17): step 4's checks of the sent XML against step 3's piBasic read, the
/// elements step 5 compares, and the read-back's comparison. No COM, no messages: Core turns its findings into errors
/// and warnings, and decides whether they may name IDs (AD-21).
/// <para>Whole Outlines (spike 2.2 constraints 1 and 2): <c>UpdatePageContent</c> replaces the OE list of every Outline
/// it is sent, so each sent Outline with an <c>objectID</c> must list every OE step 3 read under it, nested and
/// table-cell OEs included, unless the caller names it, or an OE above it, for removal.</para>
/// </summary>
public static class PageWriteCheck
{
    private static readonly XName Page = OneNoteXml.One + "Page";
    private static readonly XName Outline = OneNoteXml.One + "Outline";
    private static readonly XName OE = OneNoteXml.One + "OE";
    private static readonly XName OEChildren = OneNoteXml.One + "OEChildren";
    private static readonly XName Title = OneNoteXml.One + "Title";
    private static readonly XName QuickStyleDef = OneNoteXml.One + "QuickStyleDef";

    /// <summary>
    /// Step 4 for <c>UpdatePageContent</c>: the schema gate; the root is a <c>one:Page</c> whose <c>ID</c> is
    /// <paramref name="pageId"/>; every sent <c>objectID</c> is on the page, on an element of the same kind; no element is sent
    /// under another Outline than the one it was read in; each removal names an OE under a sent Outline and isn't also
    /// sent; and every sent Outline is whole. The first failing check is the problem. Throws
    /// <see cref="OneNoteXmlException"/> only when <paramref name="readXml"/> can't be read.
    /// </summary>
    public static PageWriteValidation Validate(string sentXml, string pageId, string readXml, IReadOnlyCollection<string> removeObjectIds)
    {
        ArgumentNullException.ThrowIfNull(sentXml);
        ArgumentNullException.ThrowIfNull(readXml);
        ArgumentNullException.ThrowIfNull(removeObjectIds);

        var read = OneNoteXml.RequireRoot(OneNoteXml.Parse(readXml), "Page");
        var schema = SchemaValidation.Problems(sentXml);
        if (schema.Count > 0)
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.InvalidXml, [], schema));
        }

        var sent = OneNoteXml.Parse(sentXml).Root!;
        if (sent.Name != Page || !string.Equals((string?)sent.Attribute("ID"), pageId, StringComparison.Ordinal))
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.PageIdMismatch, []));
        }

        var readById = ById(read);
        var sentElements = sent.Descendants().Where(e => ObjectId(e) is not null).ToList();
        var sentIds = sentElements.Select(e => ObjectId(e)!).ToHashSet(StringComparer.Ordinal);

        var unknown = sentElements.Select(e => ObjectId(e)!).Where(id => !readById.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.UnknownObjectId, unknown));
        }

        var wrong = sentElements.Where(e => readById[ObjectId(e)!].Name != e.Name).Select(e => ObjectId(e)!).Distinct(StringComparer.Ordinal).ToList();
        if (wrong.Count > 0)
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.WrongElement, wrong));
        }

        // Before the removal checks: a moved OE that is also named for removal is a move, and its userAction says how.
        // Every sent element with an objectID but an Outline counts: an OE, a table, a row or a cell moved elsewhere.
        var moved = sentElements
            .Where(e => e.Name != Outline && !SameContainer(e, readById[ObjectId(e)!]))
            .Select(e => ObjectId(e)!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (moved.Count > 0)
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.OeMoved, moved));
        }

        var sentOutlineIds = sent.Descendants(Outline).Select(ObjectId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var removals = removeObjectIds.Distinct(StringComparer.Ordinal).ToList();
        var notAnOe = removals
            .Where(id => !readById.TryGetValue(id, out var element)
                || element.Name != OE
                || ObjectId(element.Ancestors(Outline).FirstOrDefault()) is not { } outlineId
                || !sentOutlineIds.Contains(outlineId))
            .ToList();
        if (notAnOe.Count > 0)
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.RemovalNotAnOe, notAnOe));
        }

        var alsoSent = removals.Where(sentIds.Contains).ToList();
        if (alsoSent.Count > 0)
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.RemovalAlsoSent, alsoSent));
        }

        var removed = removals.ToHashSet(StringComparer.Ordinal);
        var omitted = new List<string>();
        foreach (var outline in sent.Descendants(Outline))
        {
            if (ObjectId(outline) is not { } outlineId)
            {
                continue;
            }

            var listed = outline.Descendants(OE).Select(ObjectId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var oe in readById[outlineId].Descendants(OE))
            {
                if (ObjectId(oe) is { } id
                    && !listed.Contains(id)
                    && !removed.Contains(id)
                    && !oe.Ancestors(OE).Any(a => ObjectId(a) is { } above && removed.Contains(above)))
                {
                    omitted.Add(id);
                }
            }
        }

        if (omitted.Count > 0)
        {
            return Refused(new PageWriteProblem(PageWriteProblemKind.OeOmitted, omitted.Distinct(StringComparer.Ordinal).ToList()));
        }

        var targets = sent.Descendants(Outline).Select(ObjectId).OfType<string>()
            .Concat(sent.Descendants(OE).Where(oe => !oe.Ancestors(Outline).Any()).Select(ObjectId).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new PageWriteValidation(null, targets);
    }

    /// <summary>
    /// Step 4 for <c>DeletePageContent</c> (AD-7, story 2.10): what <paramref name="objectId"/> is on step 3's piBasic read
    /// <paramref name="readXml"/>. Only a <c>one:Outline</c> may be deleted in v1; any other element with that
    /// <c>objectID</c> (an OE, the title's OE, an image, ink, a table) is <see cref="DeleteTargetKind.NotAnOutline"/>. No
    /// schema gate: nothing is sent. Throws <see cref="OneNoteXmlException"/> only when <paramref name="readXml"/> can't be
    /// read.
    /// </summary>
    public static DeleteTargetKind DeleteTarget(string readXml, string objectId)
    {
        ArgumentNullException.ThrowIfNull(readXml);
        ArgumentNullException.ThrowIfNull(objectId);
        var read = OneNoteXml.RequireRoot(OneNoteXml.Parse(readXml), "Page");
        return !ById(read).TryGetValue(objectId, out var element) ? DeleteTargetKind.Unknown
            : element.Name == Outline ? DeleteTargetKind.Outline
            : DeleteTargetKind.NotAnOutline;
    }

    /// <summary>
    /// The <c>DeletePageContent</c> read-back (AD-7): true when <paramref name="afterXml"/>, the piBasic re-read after a
    /// successful delete, still has an element with the deleted <paramref name="objectId"/>. Throws
    /// <see cref="OneNoteXmlException"/> when <paramref name="afterXml"/> can't be read.
    /// </summary>
    public static bool StillPresent(string afterXml, string objectId)
    {
        ArgumentNullException.ThrowIfNull(afterXml);
        ArgumentNullException.ThrowIfNull(objectId);
        return ById(OneNoteXml.RequireRoot(OneNoteXml.Parse(afterXml), "Page")).ContainsKey(objectId);
    }

    /// <summary>
    /// The read-back's comparison (AD-7): <paramref name="afterXml"/> is the piBasic re-read after a successful write of
    /// <paramref name="sentXml"/>. Attributes and <c>objectID</c>s are compared, never text:
    /// <list type="bullet">
    /// <item>each sent element with an <c>objectID</c> outside a sent Outline's OE lists, by ID;</item>
    /// <item>inside each sent Outline found again, the OE lists positionally at each level (nested and table-cell lists):
    /// a sent OE whose ID came back different is <see cref="ReadBackDiffKind.ObjectIdChanged"/>, a sent OE without an ID
    /// is new and only its sent attributes are compared, and a list of another length is one
    /// <see cref="ReadBackDiffKind.OeCountDiffers"/> on the Outline, which ends its walk;</item>
    /// <item>each sent Outline found again with fewer images than were sent in it is <see cref="ReadBackDiffKind.ImageLost"/>;</item>
    /// <item>with <paramref name="readXml"/> (step 3's piBasic read), each new Outline (sent without an <c>objectID</c>),
    /// matched in document order to the Outlines after the write whose ID neither step 3 read nor the call sent, is
    /// compared the same way and reported under its new ID; when the counts differ, only the first pairs are.</item>
    /// </list>
    /// Skipped: what <see cref="ElementHash.IsExcluded"/> leaves out, and a sent OE <c>style</c> that OneNote dropped
    /// because each of its declarations equals the read-back QuickStyleDef of the OE's <c>quickStyleIndex</c>. Numbers
    /// compare by value and booleans by meaning. Throws <see cref="OneNoteXmlException"/> when either XML can't be read.
    /// </summary>
    public static IReadOnlyList<ReadBackDiff> CompareReadBack(string sentXml, string afterXml, string? readXml = null)
    {
        ArgumentNullException.ThrowIfNull(sentXml);
        ArgumentNullException.ThrowIfNull(afterXml);
        var sent = OneNoteXml.RequireRoot(OneNoteXml.Parse(sentXml), "Page");
        var after = OneNoteXml.RequireRoot(OneNoteXml.Parse(afterXml), "Page");
        var afterById = ById(after);
        var diffs = new List<ReadBackDiff>();

        foreach (var outline in sent.Descendants(Outline))
        {
            if (ObjectId(outline) is not { } outlineId)
            {
                continue;
            }

            if (!afterById.TryGetValue(outlineId, out var back) || back.Name != Outline)
            {
                diffs.Add(new ReadBackDiff(ReadBackDiffKind.Missing, outlineId));
                continue;
            }

            CompareOutline(outline, back, outlineId, after, diffs);
        }

        // New Outlines (sent without an objectID), with step 3's read: matched in document order to the Outlines after the
        // write whose ID neither step 3 read nor the call sent, and compared like the others under the new ID. When the
        // counts differ, only the first pairs are compared.
        if (readXml is not null)
        {
            var readIds = ById(OneNoteXml.RequireRoot(OneNoteXml.Parse(readXml), "Page")).Keys.ToHashSet(StringComparer.Ordinal);
            var sentIds = sent.Descendants().Select(ObjectId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var newSent = sent.Descendants(Outline).Where(o => ObjectId(o) is null).ToList();
            var newAfter = after.Descendants(Outline)
                .Where(o => ObjectId(o) is { } id && !readIds.Contains(id) && !sentIds.Contains(id))
                .ToList();
            for (var i = 0; i < Math.Min(newSent.Count, newAfter.Count); i++)
            {
                CompareOutline(newSent[i], newAfter[i], ObjectId(newAfter[i])!, after, diffs);
            }
        }

        foreach (var element in sent.Descendants())
        {
            if (element.Name == Outline || ObjectId(element) is not { } id || (element.Name == OE && InSentOutline(element)))
            {
                continue;
            }

            if (!afterById.TryGetValue(id, out var back) || back.Name != element.Name)
            {
                diffs.Add(new ReadBackDiff(ReadBackDiffKind.Missing, id));
                continue;
            }

            CompareAttributes(element, back, id, after, diffs);
        }

        return diffs;
    }

    private static PageWriteValidation Refused(PageWriteProblem problem) => new(problem, []);

    private static string? ObjectId(XElement? element) => (string?)element?.Attribute("objectID") is { Length: > 0 } id ? id : null;

    /// <summary>Every element with an <c>objectID</c>, keyed by it; when an ID repeats, the first in document order keeps it.</summary>
    private static Dictionary<string, XElement> ById(XElement root)
    {
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var element in root.Descendants())
        {
            if (ObjectId(element) is { } id)
            {
                byId.TryAdd(id, element);
            }
        }

        return byId;
    }

    /// <summary>True when the element sits in the same container in both reads: the same Outline (by ID), the title, or the page.</summary>
    private static bool SameContainer(XElement sentOe, XElement readOe)
    {
        var (sentKind, sentId) = Container(sentOe);
        var (readKind, readId) = Container(readOe);
        return sentKind == readKind && (sentKind != Outline || (sentId is not null && string.Equals(sentId, readId, StringComparison.Ordinal)));
    }

    private static (XName Kind, string? Id) Container(XElement oe)
    {
        foreach (var ancestor in oe.Ancestors())
        {
            if (ancestor.Name == Outline || ancestor.Name == Title)
            {
                return (ancestor.Name, ObjectId(ancestor));
            }
        }

        return (Page, null);
    }

    private static bool InSentOutline(XElement oe) => ObjectId(oe.Ancestors(Outline).FirstOrDefault()) is not null;

    /// <summary>
    /// The OE lists directly under <paramref name="owner"/> (an Outline or an OE), in document order: each
    /// <c>one:OEChildren</c> whose nearest Outline-or-OE ancestor is the owner, so an OE's own children and its table
    /// cells' lists, but not lists inside its child OEs.
    /// </summary>
    private static List<List<XElement>> ChildLists(XElement owner) =>
        owner.Descendants(OEChildren)
            .Where(list => list.Ancestors().First(a => a.Name == OE || a.Name == Outline) == owner)
            .Select(list => list.Elements(OE).ToList())
            .ToList();

    /// <summary>One sent Outline against its read-back: its attributes, its OE lists positionally, and its images.</summary>
    private static void CompareOutline(XElement outline, XElement back, string reportId, XElement afterRoot, List<ReadBackDiff> diffs)
    {
        CompareAttributes(outline, back, reportId, afterRoot, diffs);
        if (!Walk(ChildLists(outline), ChildLists(back), afterRoot, diffs))
        {
            diffs.Add(new ReadBackDiff(ReadBackDiffKind.OeCountDiffers, reportId));
        }

        var sentImages = ImageCount.Count(outline);
        var imagesAfter = ImageCount.Count(back);
        if (imagesAfter < sentImages)
        {
            diffs.Add(new ReadBackDiff(ReadBackDiffKind.ImageLost, reportId, SentImages: sentImages, ImagesAfter: imagesAfter));
        }
    }

    /// <summary>Walks paired OE lists positionally; false at the first level whose list count or OE count differs.</summary>
    private static bool Walk(List<List<XElement>> sentLists, List<List<XElement>> backLists, XElement afterRoot, List<ReadBackDiff> diffs)
    {
        if (sentLists.Count != backLists.Count)
        {
            return false;
        }

        for (var i = 0; i < sentLists.Count; i++)
        {
            var sentList = sentLists[i];
            var backList = backLists[i];
            if (sentList.Count != backList.Count)
            {
                return false;
            }

            for (var j = 0; j < sentList.Count; j++)
            {
                var sentOe = sentList[j];
                var backOe = backList[j];
                var sentId = ObjectId(sentOe);
                var backId = ObjectId(backOe);
                if (sentId is not null && !string.Equals(sentId, backId, StringComparison.Ordinal))
                {
                    diffs.Add(new ReadBackDiff(ReadBackDiffKind.ObjectIdChanged, sentId, NewObjectId: backId));
                }

                CompareAttributes(sentOe, backOe, backId ?? sentId ?? string.Empty, afterRoot, diffs);
                if (!Walk(ChildLists(sentOe), ChildLists(backOe), afterRoot, diffs))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void CompareAttributes(XElement sent, XElement back, string reportId, XElement afterRoot, List<ReadBackDiff> diffs)
    {
        foreach (var attribute in sent.Attributes())
        {
            if (attribute.IsNamespaceDeclaration
                || attribute.Name.Namespace != XNamespace.None
                || attribute.Name.LocalName == "objectID"
                || ElementHash.IsExcluded(attribute))
            {
                continue;
            }

            var name = attribute.Name.LocalName;
            var backValue = (string?)back.Attribute(name);
            if (backValue is null)
            {
                if (name == "style" && sent.Name == OE && StyleIsItsQuickStyle(attribute.Value, sent, back, afterRoot))
                {
                    continue;
                }

                diffs.Add(new ReadBackDiff(ReadBackDiffKind.AttributeDiffers, reportId, name));
            }
            else if (!SameValue(attribute.Value, backValue))
            {
                diffs.Add(new ReadBackDiff(ReadBackDiffKind.AttributeDiffers, reportId, name));
            }
        }
    }

    private static bool SameValue(string sent, string back)
    {
        if (string.Equals(sent, back, StringComparison.Ordinal))
        {
            return true;
        }

        if (Number(sent) is { } a && Number(back) is { } b)
        {
            return a == b;
        }

        return Boolean(sent) is { } x && Boolean(back) is { } y && x == y;
    }

    private static double? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static bool? Boolean(string value) => value switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    /// <summary>
    /// AD-7's <c>side-local-style-duplicate</c> exemption: OneNote drops an OE <c>style</c> equal to its QuickStyleDef. True
    /// when every declaration is <c>font-family</c>, <c>font-size</c> (in pt) or <c>color</c> and equals the read-back
    /// QuickStyleDef for the OE's <c>quickStyleIndex</c> (its read-back value, else the sent one).
    /// </summary>
    private static bool StyleIsItsQuickStyle(string style, XElement sentOe, XElement backOe, XElement afterRoot)
    {
        var index = (string?)backOe.Attribute("quickStyleIndex") ?? (string?)sentOe.Attribute("quickStyleIndex");
        if (index is null
            || afterRoot.Elements(QuickStyleDef).FirstOrDefault(d => SameValue((string?)d.Attribute("index") ?? string.Empty, index)) is not { } definition)
        {
            return false;
        }

        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = declaration.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return false;
            }

            var property = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            var matches = property switch
            {
                "font-family" => string.Equals(value.Trim('\'', '"'), (string?)definition.Attribute("font"), StringComparison.OrdinalIgnoreCase),
                "font-size" => value.EndsWith("pt", StringComparison.Ordinal)
                    && Number(value[..^2].Trim()) is { } size
                    && Number((string?)definition.Attribute("fontSize") ?? string.Empty) is { } defined
                    && size == defined,
                "color" => string.Equals(value, (string?)definition.Attribute("fontColor"), StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
