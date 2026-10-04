using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.ValueObjects;

public class AutoReplyRule
{
    public const int MaxKeywordsLength = 300;
    public const int MaxReplyLength = 500;
    public const int MaxInboxLength = 500;

    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Comma separated words; a comment containing one of them matches.</summary>
    public string Keywords { get; set; } = "";
    public string Reply { get; set; } = "";
    public string Inbox { get; set; } = "";
    /// <summary>"all", or the id of the collection whose posts the rule is about.</summary>
    public string Scope { get; set; } = "all";
    public bool On { get; set; } = true;
}

/// <summary>Auto-reply rules of a workspace (a Pro feature). Stored only: nothing reads Facebook comments yet.</summary>
public class AutoReplySettings
{
    public const int MaxRules = 50;

    public bool On { get; set; }
    public List<AutoReplyRule> Rules { get; set; } = [];

    public void Validate()
    {
        if (Rules.Count > MaxRules) throw new DomainException($"ตั้งกฎตอบอัตโนมัติได้ไม่เกิน {MaxRules} กฎ");
        foreach (var r in Rules)
        {
            r.Keywords = (r.Keywords ?? "").Trim();
            r.Reply = (r.Reply ?? "").Trim();
            r.Inbox = (r.Inbox ?? "").Trim();
            r.Scope = string.IsNullOrWhiteSpace(r.Scope) ? "all" : r.Scope.Trim();
            if (r.Keywords.Length == 0) throw new DomainException("กรุณาใส่คำที่ต้องการให้ตรวจจับ");
            if (r.Reply.Length == 0 && r.Inbox.Length == 0) throw new DomainException("กรุณาใส่ข้อความตอบคอมเมนต์หรือข้อความทักแชทอย่างน้อย 1 อย่าง");
            if (r.Keywords.Length > AutoReplyRule.MaxKeywordsLength) throw new DomainException($"คำที่ตรวจจับยาวเกิน {AutoReplyRule.MaxKeywordsLength} ตัวอักษร");
            if (r.Reply.Length > AutoReplyRule.MaxReplyLength) throw new DomainException($"ข้อความตอบคอมเมนต์ยาวเกิน {AutoReplyRule.MaxReplyLength} ตัวอักษร");
            if (r.Inbox.Length > AutoReplyRule.MaxInboxLength) throw new DomainException($"ข้อความทักแชทยาวเกิน {AutoReplyRule.MaxInboxLength} ตัวอักษร");
            if (r.Scope.Length > 40) throw new DomainException("ขอบเขตของกฎไม่ถูกต้อง");
        }
        if (Rules.Select(r => r.Id).Distinct().Count() != Rules.Count) throw new DomainException("รหัสกฎซ้ำกัน");
    }
}
