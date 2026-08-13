// Recognising an ISOXML card that has nothing in it.
//
// A New Holland / CNH Voyager2 card (`*.cn1`) ships a TASKDATA.XML that is a
// 208-byte stub: a well-formed <ISO11783_TaskData> element with no children.
// The real data is beside it in CNH's own `.agp/.nav/.pls/.agf` files, which no
// ADAPT plugin reads. The ISOv4 plugin sees the stub, answers "yes, I can read
// this card", and then returns nothing.
//
// A vault sweep found 18 directories in exactly that state. Left alone the user
// is told the card is supported and handed an empty import, which looks like a
// broken plugin rather than an unsupported format. Naming it costs one file
// read and turns a mystery into an answer.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SmsLibre.Import;

public static class Isoxml
{
    /// <summary>
    /// The task controller named in a placeholder TASKDATA, or null when the
    /// folder has no TASKDATA or that TASKDATA actually carries data.
    /// </summary>
    public static string? PlaceholderTaskData(string cardDir)
    {
        string? file = FindTaskData(cardDir);
        if (file is null) return null;

        try
        {
            var root = XDocument.Load(file).Root;
            if (root is null || root.Elements().Any()) return null;   // has content

            string maker = root.Attribute("TaskControllerManufacturer")?.Value ?? "";
            return maker.Length > 0 ? maker : "unknown";
        }
        catch { return null; }   // unreadable or malformed: not our call to make
    }

    /// <summary>The card's TASKDATA.XML, searched a couple of levels down.</summary>
    private static string? FindTaskData(string cardDir)
    {
        try
        {
            // Displays nest it as TASKDATA/TASKDATA.XML, and CNH one deeper
            // still (<card>.cn1/<card>.cn1/xml/TaskData.xml), so search rather
            // than guess — but stop before this becomes a full tree walk.
            return Directory.EnumerateFiles(cardDir, "TASKDATA.XML",
                                            SearchOption.AllDirectories)
                            .FirstOrDefault();
        }
        catch { return null; }
    }
}
