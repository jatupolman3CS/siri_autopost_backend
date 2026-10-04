namespace SIRIAUTOPOST.Domain.Exceptions;

// Two requests raced and the database refused one of them: a deadlock or serialization failure, a lock that was not
// granted in time, or a row that changed after it was read (a concurrency token). Nothing of the loser was saved, so
// running it again from a fresh read usually works. The API turns it into 409.
public class ConcurrencyConflictException(string? message = null)
    : ConflictException(message ?? "ข้อมูลถูกแก้ไขพร้อมกันโดยคำขออื่น กรุณาลองใหม่อีกครั้ง");
