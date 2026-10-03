namespace SIRIAUTOPOST.Domain.Exceptions;

// A unique index refused a row that is already there (a retried webhook, two saves racing). The API turns it into 409.
public class DuplicateKeyException(string constraint) : ConflictException("ข้อมูลนี้มีอยู่แล้ว")
{
    public string Constraint { get; } = constraint;
}
