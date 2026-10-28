using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KodaMemo.Shared.Models;

namespace KodaMemo.Shared.Services;

/// <summary>Checks whether extracted quotations occur in a meeting transcript.</summary>
/// <remarks>A textual match is not proof that an interpretation or assignment is correct.</remarks>
public static class SummaryEvidenceValidator
{
    /// <summary>Updates citation and entity-presence indicators without confirming content for the user.</summary>
    /// <param name="session">The meeting whose transcript and extracted items are checked.</param>
    public static void ValidateMeetingEvidence(MeetingSessionDto session)
    {
        string fullTranscript = string.Join(" ", session.Segments.Select(s => s.Text));
        string normalizedTranscript = NormalizeForMatch(fullTranscript);

        foreach (var topic in session.MainTopics)
        {
            topic.CitationStatus = EvaluateQuote(normalizedTranscript, topic.EvidenceQuote);
        }

        foreach (var agreement in session.Agreements)
        {
            agreement.CitationStatus = EvaluateQuote(normalizedTranscript, agreement.EvidenceQuote);
        }

        foreach (var task in session.Tasks)
        {
            task.CitationStatus = EvaluateQuote(normalizedTranscript, task.EvidenceQuote);
            task.AssigneeVerifiedInTranscript = IsEntityMentioned(normalizedTranscript, task.Assignee, "no especificado");
            task.DueDateVerifiedInTranscript = IsEntityMentioned(normalizedTranscript, task.DueDateText, "no especificada");
        }
    }

    /// <summary>Classifies a quotation against an already normalized transcript.</summary>
    /// <param name="normalizedTranscript">The transcript returned by <see cref="NormalizeForMatch"/>.</param>
    /// <param name="quote">The original quotation to search for.</param>
    /// <returns>The match classification, independent of human confirmation.</returns>
    public static CitationMatchStatus EvaluateQuote(string normalizedTranscript, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(normalizedTranscript))
        {
            return CitationMatchStatus.QuoteNotFound;
        }

        string normQuote = NormalizeForMatch(quote);
        if (normQuote.Length == 0)
        {
            return CitationMatchStatus.QuoteNotFound;
        }

        if (normalizedTranscript.Contains(normQuote, StringComparison.Ordinal))
        {
            return CitationMatchStatus.QuoteFoundExact;
        }

        string[] words = normQuote.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToArray();

        if (words.Length == 0)
        {
            return CitationMatchStatus.QuoteNotFound;
        }

        int matched = words.Count(w => normalizedTranscript.Contains(w, StringComparison.Ordinal));
        double ratio = (double)matched / words.Length;

        return ratio >= 0.65
            ? CitationMatchStatus.QuoteFoundPartial
            : CitationMatchStatus.QuoteNotFound;
    }

    private static bool IsEntityMentioned(string normalizedTranscript, string? entityValue, string defaultPlaceholder)
    {
        if (string.IsNullOrWhiteSpace(entityValue))
        {
            return true;
        }

        string norm = NormalizeForMatch(entityValue);
        if (norm.Contains(defaultPlaceholder, StringComparison.Ordinal))
        {
            return true;
        }

        string[] tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .ToArray();

        return tokens.Length == 0 || tokens.Any(t => normalizedTranscript.Contains(t, StringComparison.Ordinal));
    }

    public static string NormalizeForMatch(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string lower = input.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lower.Length);
        foreach (char c in lower)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append(' ');
            }
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static string ComputeSha256(string content)
    {
        string normalized = (content ?? string.Empty).Replace("\r\n", "\n").Trim();
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>Builds a navigable topic tree from existing meeting data without model calls.</summary>
public static class MindMapGenerator
{
    /// <summary>
    /// Construye un mapa mental jerÃ¡rquico editable a partir de los temas, acuerdos y tareas ya existentes
    /// en la sesiÃ³n sin realizar llamadas adicionales a modelos de IA (Costo = US$0.00).
    /// </summary>
    /// <summary>Replaces the generated tree with branches for topics, agreements and tasks.</summary>
    /// <param name="session">The meeting supplying the tree content.</param>
    /// <returns>The root assigned to the meeting.</returns>
    /// <remarks>Manual tree edits should be exported before rebuilding.</remarks>
    public static MindMapNodeDto BuildFromExistingSummary(MeetingSessionDto session)
    {
        var root = new MindMapNodeDto
        {
            Label = session.Title,
            Category = "root"
        };

        var topicsBranch = new MindMapNodeDto
        {
            Label = $"Temas Principales ({session.MainTopics.Count})",
            Category = "topic"
        };
        foreach (var t in session.MainTopics)
        {
            var node = new MindMapNodeDto
            {
                Label = t.Title,
                Category = "topic",
                ReferenceTimestamp = t.TimestampReference
            };
            if (!string.IsNullOrWhiteSpace(t.SummaryNotes))
            {
                node.Children.Add(new MindMapNodeDto
                {
                    Label = t.SummaryNotes,
                    Category = "custom",
                    ReferenceTimestamp = t.TimestampReference
                });
            }
            topicsBranch.Children.Add(node);
        }

        var agreementsBranch = new MindMapNodeDto
        {
            Label = $"Acuerdos ({session.Agreements.Count})",
            Category = "agreement"
        };
        foreach (var a in session.Agreements)
        {
            agreementsBranch.Children.Add(new MindMapNodeDto
            {
                Label = a.Description,
                Category = "agreement",
                ReferenceTimestamp = a.TimestampReference
            });
        }

        var tasksBranch = new MindMapNodeDto
        {
            Label = $"Tareas y Pendientes ({session.Tasks.Count})",
            Category = "task"
        };
        foreach (var task in session.Tasks)
        {
            tasksBranch.Children.Add(new MindMapNodeDto
            {
                Label = $"{task.Description} [{task.Assignee} â€” {task.DueDateText}]",
                Category = "task",
                ReferenceTimestamp = task.TimestampReference
            });
        }

        root.Children.Add(topicsBranch);
        root.Children.Add(agreementsBranch);
        root.Children.Add(tasksBranch);

        session.MindMapRoot = root;
        return root;
    }
}

/// <summary>Measures word edit distance against a reference transcript.</summary>
public static class WerCalculator
{
    /// <summary>
    /// Calcula Word Error Rate (WER) mediante distancia de Levenshtein a nivel de palabras
    /// entre un texto de referencia y una transcripciÃ³n evaluada, normalizando puntuaciÃ³n y mayÃºsculas.
    /// </summary>
    public static (double WerFraction, int EditDistance, int ReferenceWordCount, int HypothesisWordCount) ComputeWordErrorRate(
        string referenceText,
        string hypothesisText)
    {
        string[] refWords = SummaryEvidenceValidator.NormalizeForMatch(referenceText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] hypWords = SummaryEvidenceValidator.NormalizeForMatch(hypothesisText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (refWords.Length == 0)
        {
            return (hypWords.Length == 0 ? 0.0 : 1.0, hypWords.Length, 0, hypWords.Length);
        }

        int n = refWords.Length;
        int m = hypWords.Length;
        int[,] dp = new int[n + 1, m + 1];

        for (int i = 0; i <= n; i++) dp[i, 0] = i;
        for (int j = 0; j <= m; j++) dp[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = string.Equals(refWords[i - 1], hypWords[j - 1], StringComparison.Ordinal) ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        int edits = dp[n, m];
        double wer = (double)edits / n;
        return (wer, edits, n, m);
    }
}
