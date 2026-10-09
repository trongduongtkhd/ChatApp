using System.Security.Cryptography;
using System.Text;

namespace ChatApp.Contracts;

// Quy ước chung cho chat riêng 2 người (Phần 11). identity-service (trả directGroupId trong danh sách bạn)
// và group-service (tạo nhóm khi nhận friendship-changed) PHẢI tính ra cùng một kết quả,
// nên công thức nằm ở hợp đồng dùng chung chứ không viết riêng ở từng service.
public static class DirectChat
{
    // Namespace cố định của UUID v5 cho chat riêng. KHÔNG ĐƯỢC ĐỔI: đổi là mọi mã nhóm riêng đã tạo bị lệch.
    private static readonly Guid Namespace = new("3b1f8c2e-7d4a-4e9b-9c61-5a2f0e8d7b14");

    // Sắp cặp userId: low < high. So chuỗi GUID chữ thường kiểu ordinal (ký tự '0'-'9' < 'a'-'f')
    // = so từng byte theo thứ tự viết → khớp cách PostgreSQL so kiểu uuid (CHECK user_low_id < user_high_id).
    // Viết rõ ra thay vì dùng Guid.CompareTo để không phải dựa vào chi tiết cài đặt của .NET.
    public static (Guid Low, Guid High) Order(Guid a, Guid b)
    {
        if (a == b)
            throw new ArgumentException("Hai userId phải khác nhau", nameof(b));
        return string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);
    }

    // Kafka key của sự kiện bạn bè: mọi sự kiện của cùng một cặp → cùng partition → đúng thứ tự.
    public static string PairKey(Guid a, Guid b)
    {
        var (low, high) = Order(a, b);
        return $"{low}:{high}";
    }

    // Mã nhóm chat riêng TẤT ĐỊNH: cùng một cặp (theo thứ tự nào cũng vậy) luôn ra cùng một GUID,
    // ở máy nào, lần nào cũng vậy → nhận sự kiện "Accepted" 2 lần thì lần sau INSERT trùng PK, không tạo nhóm thứ hai.
    public static Guid GroupIdFor(Guid a, Guid b)
    {
        var (low, high) = Order(a, b);
        return CreateV5(Namespace, $"direct:{low}:{high}");
    }

    // UUID v5 (RFC 9562): SHA-1(namespace || tên) → lấy 16 byte đầu, ghi đè 4 bit version = 5 và 2 bit variant = 10.
    // Khác GUID v7 (ngẫu nhiên + thời gian): v5 không có thứ tự thời gian, nhưng đầu vào giống nhau → kết quả giống nhau.
    public static Guid CreateV5(Guid ns, string name)
    {
        // bigEndian: true → byte theo đúng thứ tự viết của chuỗi GUID (chuẩn RFC), không theo kiểu lưu trong bộ nhớ của .NET.
        var nsBytes = ns.ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[nsBytes.Length + nameBytes.Length];
        nsBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, nsBytes.Length);
        var hash = SHA1.HashData(input);

        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant RFC
        return new Guid(bytes, bigEndian: true);
    }
}
