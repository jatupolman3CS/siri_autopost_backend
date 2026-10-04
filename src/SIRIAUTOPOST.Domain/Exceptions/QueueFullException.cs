namespace SIRIAUTOPOST.Domain.Exceptions;

// A schedule run would take the workspace over its limit of queued posts. A business rule (422) for the person who asked
// for it; the background top-up logs it and tries again later.
public class QueueFullException(int queued, int adding, int limit)
    : DomainException(
        $"คิวโพสต์ของเวิร์กสเปซเต็ม: ตอนนี้มีโพสต์รอโพสต์อยู่ {queued:N0} โพสต์ ตารางนี้จะเพิ่มอีก {adding:N0} โพสต์ เกินกว่าที่เก็บได้ {limit:N0} โพสต์ " +
        "ลบตารางหรือโพสต์ที่ไม่ใช้แล้ว หรือรอให้โพสต์ที่คิวไว้ถูกส่งก่อน แล้วลองใหม่")
{
    public int Queued { get; } = queued;
    public int Adding { get; } = adding;
}
