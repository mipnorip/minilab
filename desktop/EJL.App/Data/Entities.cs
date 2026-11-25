namespace EJL.Data;

// ---------- auth ----------

public class User
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "engineer"; // admin | head | quality_manager | engineer
    public bool CanWfPrecursors { get; set; }
    public bool IsActive { get; set; } = true;
}

// ---------- forms ----------

public class Template
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsPreset { get; set; }
    public int? CurrentVersionId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class TemplateVersion
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public int Version { get; set; }
    public List<EJL.Domain.FormColumn> Columns { get; set; } = new();
    public string? Comment { get; set; }
    public int? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ---------- records ----------

public class Journal
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "form"; // form | materials
    public int? FormTemplateId { get; set; }
    public int? FormVersionId { get; set; }
    public string? MaterialsType { get; set; } // reagent | so | precursor
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public int CheckPeriodWeeks { get; set; } = 4;
    public int RetentionYears { get; set; } = 5;
    public string Status { get; set; } = "active"; // active | closed | archived
    public bool Daily { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastReviewAt { get; set; }
}

public class JournalMember
{
    public int JournalId { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "worker"; // owner | worker
}

public class RecordEntity
{
    public long Id { get; set; }
    public int JournalId { get; set; }
    public int RowNo { get; set; }
    public Dictionary<string, object?> Data { get; set; } = new();
    public string Status { get; set; } = "entered"; // entered | closed
    public int AuthorId { get; set; }
    public string AuthorName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Revision
{
    public long Id { get; set; }
    public long RecordId { get; set; }
    public Dictionary<string, object?> OldData { get; set; } = new();
    public Dictionary<string, object?> NewData { get; set; } = new();
    public string ReasonCode { get; set; } = ""; // V | O | I
    public string? ReasonComment { get; set; }
    public int AuthorId { get; set; }
    public string AuthorName { get; set; } = "";
    public DateTime Ts { get; set; }
}

public class ReviewLog
{
    public long Id { get; set; }
    public int JournalId { get; set; }
    public int ReviewerId { get; set; }
    public string ReviewerName { get; set; } = "";
    public string? Note { get; set; }
    public DateTime Ts { get; set; }
}

// ---------- materials ----------

public class MaterialItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = ""; // reagent | so | precursor
    public string Unit { get; set; } = "";
    public string StoragePlace { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class Batch
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public string BatchNo { get; set; } = "";
    public double Qty { get; set; }
    public string? ExpiresOn { get; set; } // ISO «yyyy-MM-dd»
    public DateTime ReceivedAt { get; set; }
    public string ReceivedByName { get; set; } = "";
    public string Status { get; set; } = "active"; // active | blocked
}

public class Operation
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    public int ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string OpType { get; set; } = ""; // receipt | writeoff | inventory | check
    public int? BatchId { get; set; }
    public string? BatchNo { get; set; }
    public double Qty { get; set; }
    public string Unit { get; set; } = "";
    public int? ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string? Note { get; set; }
    public string? TargetJournalCode { get; set; }
    public int? TargetRecordId { get; set; }
}

// ---------- scheduler ----------

public class Notification
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Type { get; set; } = ""; // material_expiring | daily_missing | review_due
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public bool IsRead { get; set; }
    public string DedupeKey { get; set; } = ""; // уникальный: не чаще раза в сутки
}

// ---------- audit ----------

public class AuditEvent
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    public int? ActorId { get; set; }
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? EntityTitle { get; set; }
    public string? Reason { get; set; }
    public Dictionary<string, object?> Payload { get; set; } = new();
}
