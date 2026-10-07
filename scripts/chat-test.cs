#:package Microsoft.AspNetCore.SignalR.Client@10.0.12
#:package Confluent.Kafka@2.15.1
#:property PublishAot=false

// Client test SignalR cho chat-service (Phần 7). File C# chạy thẳng (.NET 10 file-based app), không cần project.
// Mọi request đi qua Gateway (5000) giống Angular: REST /api/..., SignalR /hubs/chat.
//
// Cần chạy trước: docker compose up -d, identity, group, Gateway, chat-service.
// Chạy (tại thư mục gốc):
//   dotnet run scripts/chat-test.cs -- basic   # 2 user chat với nhau, người ngoài bị chặn, không token bị 401
//   dotnet run scripts/chat-test.cs -- grpc    # kiểm tra thành viên (qua gRPC) khi thêm/xóa thành viên, xóa nhóm
//   dotnet run scripts/chat-test.cs -- dup     # gửi trùng messageId (tuần tự + đồng thời) → chỉ một tin
//   dotnet run scripts/chat-test.cs -- load    # bắn 200 tin song song (--count 200 --connections 20) → seq không trùng, không thiếu
//   dotnet run scripts/chat-test.cs -- seqlost # làm hỏng bộ đếm Redis (xóa key, lùi số) → vẫn cấp đúng số
//   dotnet run scripts/chat-test.cs -- cache   # cache thành viên Redis: nạp, TTL, bị xóa khi thêm/xóa thành viên
//   dotnet run scripts/chat-test.cs -- presence # online/offline: nhiều kết nối của một user, đóng lần lượt
//   dotnet run scripts/chat-test.cs -- history # REST lịch sử: lật trang bằng beforeSeq, lỗi 400/401/403/404
//   dotnet run scripts/chat-test.cs -- down    # kịch bản lỗi: tắt group-service giữa chừng (nhóm có cache vẫn chat được) rồi bật lại
//   dotnet run scripts/chat-test.cs -- backplane # Phần 8: 2 user nối THẲNG vào 2 bản chat-service khác nhau → có thấy tin nhau không
//   dotnet run scripts/chat-test.cs -- lb      # Phần 8: Nginx chia request/kết nối cho 2 bản; có negotiate thì lỗi 404
//   dotnet run scripts/chat-test.cs -- failover # Phần 8: đang chat thì tắt bản chat-service giữ kết nối → tự nối lại sang bản kia
//                                               # --action stop (tắt đúng cách, mặc định) | kill (giết đột ngột) | none (tự tắt tay)
//   dotnet run scripts/chat-test.cs -- reorder  # Phần 9: member-removed đến TRƯỚC member-added (2 topic khác nhau) → bản sao thành viên
//                                               # của notification-service có còn đúng không (cần chạy notification-service)
//   dotnet run scripts/chat-test.cs -- snapshot # Phần 9: so bản sao thành viên (notification_db) với dữ liệu gốc (group_db), dùng sau replay
//   dotnet run scripts/chat-test.cs -- unread   # Phần 9: UnreadCountChanged qua /hubs/notifications (2 tab), đánh dấu đã đọc, tăng lại
//   dotnet run scripts/chat-test.cs -- dupevent # Phần 9: đẩy cùng một MessageSent 3 lần → chỉ đếm 1 (Idempotent Consumer)
//                                               # tắt công tắc để thấy sai: $env:Notification__IdempotentConsumer="false" rồi chạy notification-service
// Tùy chọn: --user duong --other lan --outsider minh --password 123456 --gateway http://localhost:5000 --count 200 --connections 20
//           --a http://localhost:5003 --b http://localhost:5013 (địa chỉ trực tiếp của bản 1, bản 2)
//           --negotiate  (bật lại negotiate của SignalR cho mọi chế độ → demo lỗi khi đứng sau Nginx round-robin)
//           --kafka localhost:9092 (chế độ tự đẩy sự kiện thẳng lên Kafka: reorder, dupevent)
//
// Từ Phần 8 kết nối hub mặc định BỎ negotiate (SkipNegotiation + chỉ WebSocket): mở thẳng WebSocket bằng MỘT request,
// nên Nginx chia round-robin thoải mái mà không cần sticky session.
//
// Lần chạy đầu chậm (~20 giây) vì phải tải package và biên dịch; các lần sau nhanh.

using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

var opt = Options.Parse(args);
var api = new Api(opt.Gateway);
ChatClient.Negotiate = opt.Negotiate;

try
{
    var ok = opt.Mode switch
    {
        "basic" => await Modes.Basic(api, opt),
        "grpc" => await Modes.Grpc(api, opt),
        "dup" => await Modes.Dup(api, opt),
        "load" => await Modes.Load(api, opt),
        "seqlost" => await Modes.SeqLost(api, opt),
        "cache" => await Modes.Cache(api, opt),
        "presence" => await Modes.Presence(api, opt),
        "history" => await Modes.History(api, opt),
        "down" => await Modes.Down(api, opt),
        "backplane" => await Modes.Backplane(api, opt),
        "lb" => await Modes.Lb(api, opt),
        "failover" => await Modes.Failover(api, opt),
        "reorder" => await Modes.Reorder(api, opt),
        "snapshot" => await Modes.Snapshot(api, opt),
        "unread" => await Modes.Unread(api, opt),
        "dupevent" => await Modes.DupEvent(api, opt),
        _ => throw new ArgumentException($"Chế độ không hợp lệ: '{opt.Mode}'. Dùng: basic | grpc | dup | load | seqlost | cache | presence | history | down | backplane | lb | failover | reorder | snapshot | unread | dupevent")
    };
    Out.Result(ok);
    return ok ? 0 : 1;
}
catch (Exception ex)
{
    Out.Fail($"LỖI: {ex.Message}");
    return 2;
}

static class Modes
{
    // Bước 2: A tạo nhóm, thêm B; A và B vào phòng; A gửi 3 tin, B gửi 1 tin → cả hai nhận đủ 4 tin, seq tăng dần.
    // Người ngoài (C) JoinGroup / SendMessage → bị từ chối. Kết nối không token → 401.
    public static async Task<bool> Basic(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo chat");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: {a.UserName} (Owner) + {b.UserName}");

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);
        await connA.Hub.InvokeAsync("JoinGroup", groupId);
        await connB.Hub.InvokeAsync("JoinGroup", groupId);
        // ConnectionId do negotiate trả về → bỏ negotiate thì client không biết (server vẫn có, xem log chat-service).
        Out.Info($"Đã kết nối hub: {a.UserName} = {connA.Hub.ConnectionId ?? "(bỏ negotiate)"}, {b.UserName} = {connB.Hub.ConnectionId ?? "(bỏ negotiate)"}");

        var sent = new List<MessageDto>();
        foreach (var text in new[] { "Xin chào", "Mình là " + a.UserName, "Test SignalR" })
            sent.Add(await connA.SendAsync(groupId, text));
        sent.Add(await connB.SendAsync(groupId, "Chào " + a.UserName));
        foreach (var m in sent)
            Out.Info($"  gửi xong  seq={m.SequenceNumber,-4} {m.SenderName}: {m.Content}");

        await Task.Delay(500); // chờ ReceiveMessage tới nơi
        var checks = new List<bool>
        {
            Out.Check($"{b.UserName} nhận đủ 4 tin", connB.Received.Count == 4, $"nhận {connB.Received.Count}"),
            Out.Check($"{a.UserName} (người gửi) cũng nhận đủ 4 tin", connA.Received.Count == 4, $"nhận {connA.Received.Count}"),
            Out.Check("seq tăng liên tiếp", IsConsecutive(sent.Select(m => m.SequenceNumber)),
                string.Join(", ", sent.Select(m => m.SequenceNumber)))
        };

        // Người ngoài nhóm: đăng nhập được nhưng không vào phòng, không gửi tin được.
        var c = await api.TryLoginAsync(opt.Outsider, opt.Password);
        if (c is null)
            Out.Warn($"Không đăng nhập được '{opt.Outsider}' → bỏ qua kiểm tra người ngoài (dùng --outsider <user>)");
        else
        {
            await using var connC = await ChatClient.ConnectAsync(opt.Gateway, c);
            checks.Add(await Out.ExpectHubError($"{c.UserName} (ngoài nhóm) JoinGroup bị từ chối",
                () => connC.Hub.InvokeAsync("JoinGroup", groupId)));
            checks.Add(await Out.ExpectHubError($"{c.UserName} (ngoài nhóm) SendMessage bị từ chối",
                () => connC.SendAsync(groupId, "nghe lén")));
            await Task.Delay(300);
            checks.Add(Out.Check($"{b.UserName} không nhận tin của người ngoài", connB.Received.Count == 4, $"nhận {connB.Received.Count}"));
        }

        // Không token: SignalR gọi /hubs/chat/negotiate → Gateway trả 401.
        try
        {
            await using var anon = new HubConnectionBuilder().WithUrl($"{opt.Gateway}/hubs/chat").Build();
            await anon.StartAsync();
            checks.Add(Out.Check("Kết nối không token bị từ chối", false, "lại kết nối được!"));
        }
        catch (HttpRequestException ex)
        {
            checks.Add(Out.Check("Kết nối không token bị từ chối", ex.StatusCode == System.Net.HttpStatusCode.Unauthorized, $"{(int?)ex.StatusCode}"));
        }

        Out.Info($"\nXem trong DB: docker exec postgres psql -U chatapp -d chat_db -c \"select sequence_number, sender_name, content from messages where group_id = '{groupId}' order by sequence_number\"");
        Out.Info($"Xem bộ đếm:  docker exec redis redis-cli GET chat:seq:{groupId}");
        return checks.All(x => x);
    }

    // Thay cho scripts/test-grpc.ps1 bản Phần 6 (endpoint /debug/membership đã xóa).
    // Từ Bước 5 hub hỏi cache Redis trước, cache trống mới gọi gRPC → thêm/xóa thành viên có hiệu lực sau ~1–2 giây
    // (chờ Kafka xóa cache), không còn "ngay lập tức" như khi gọi gRPC mỗi lần. Script đo khoảng trễ đó.
    public static async Task<bool> Grpc(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo gRPC");
        Out.Info($"Nhóm {groupId}: Owner = {a.UserName}, người thứ hai = {b.UserName}");

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);

        var checks = new List<bool>
        {
            await Out.ExpectOk($"Owner ({a.UserName}) JoinGroup", () => connA.Hub.InvokeAsync("JoinGroup", groupId)),
            await Out.ExpectHubError($"{b.UserName}, chưa được thêm → JoinGroup bị từ chối", () => connB.Hub.InvokeAsync("JoinGroup", groupId))
        };

        await api.AddMemberAsync(a, groupId, b.UserId);
        checks.Add(await Out.Eventually($"{b.UserName}, vừa được thêm → JoinGroup được", expectOk: true,
            () => connB.Hub.InvokeAsync("JoinGroup", groupId)));

        await api.DeleteGroupAsync(a, groupId);
        checks.Add(await Out.Eventually($"Owner, sau khi xóa nhóm → JoinGroup bị từ chối", expectOk: false,
            () => connA.Hub.InvokeAsync("JoinGroup", groupId)));

        Out.Info("\nDemo lỗi (group-service chết giữa chừng): dotnet run scripts/chat-test.cs -- down");
        return checks.All(x => x);
    }

    // Bước 3 – idempotency: gửi CÙNG messageId nhiều lần (tuần tự, rồi đồng thời từ 5 kết nối) → chỉ MỘT tin.
    public static async Task<bool> Dup(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo gửi trùng");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: {a.UserName} (Owner) + {b.UserName}\n");

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);
        await connB.Hub.InvokeAsync("JoinGroup", groupId);
        var checks = new List<bool>();

        // 1. Tuần tự: giống client không nhận được trả lời nên gửi lại.
        var id1 = Guid.CreateVersion7();
        var first = await connA.SendAsync(groupId, "Tin gửi 2 lần (tuần tự)", id1);
        var again = await connA.SendAsync(groupId, "Tin gửi 2 lần (tuần tự)", id1);
        Out.Info($"Tuần tự  messageId {id1}: lần 1 seq={first.SequenceNumber}, lần 2 seq={again.SequenceNumber}");
        checks.Add(Out.Check("Lần 2 trả về đúng tin cũ (cùng seq)", first.SequenceNumber == again.SequenceNumber));

        // 2. Đồng thời: 5 kết nối của cùng user gửi cùng messageId một lúc.
        //    (Một kết nối SignalR xử lý lần lượt từng lời gọi, nên phải dùng nhiều kết nối mới thật sự song song.)
        var id2 = Guid.CreateVersion7();
        var conns = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ChatClient.ConnectAsync(opt.Gateway, a)));
        var results = await Task.WhenAll(conns.Select(c => c.SendAsync(groupId, "Tin gửi 5 lần (đồng thời)", id2)));
        foreach (var c in conns) await c.DisposeAsync();
        Out.Info($"Đồng thời messageId {id2}: seq trả về = {string.Join(", ", results.Select(r => r.SequenceNumber))}");
        checks.Add(Out.Check("Cả 5 lần trả về cùng một tin", results.Select(r => r.SequenceNumber).Distinct().Count() == 1));

        // 3. Người khác dùng lại messageId của tin đã có → bị từ chối, không lộ nội dung.
        checks.Add(await Out.ExpectHubError($"{b.UserName} dùng lại messageId của {a.UserName} → bị từ chối",
            () => connB.SendAsync(groupId, "đoán id", id1)));

        // 4. Một tin bình thường để thấy số tiếp theo (có thể "nhảy" nếu ở bước 2 có lần INCR bị bỏ phí).
        var next = await connA.SendAsync(groupId, "Tin bình thường sau cùng");
        Out.Info($"Tin bình thường tiếp theo: seq={next.SequenceNumber}");

        await Task.Delay(500);
        checks.Add(Out.Check($"{b.UserName} chỉ nhận 3 ReceiveMessage (không nhận bản trùng)", connB.Received.Count == 3,
            $"nhận {connB.Received.Count}: seq {string.Join(", ", connB.Received.Select(m => m.SequenceNumber))}"));

        Out.Info($"\nDB phải có đúng 3 tin và 3 dòng outbox:");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select sequence_number, id, content from messages where group_id = '{groupId}' order by sequence_number\"");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select event_type, key, processed_at, payload->>'sequenceNumber' as seq from outbox_messages where key = '{groupId}' order by occurred_at\"");
        return checks.All(x => x);
    }

    // Bước 4 – bắn N tin song song (mặc định 200 tin, 20 kết nối, 2 user) → seq không trùng, không thiếu.
    public static async Task<bool> Load(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo 200 tin");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: bắn {opt.Count} tin qua {opt.Connections} kết nối ({a.UserName} + {b.UserName})");

        // Người nghe: một kết nối của B ngồi trong phòng, đếm ReceiveMessage.
        await using var listener = await ChatClient.ConnectAsync(opt.Gateway, b);
        await listener.Hub.InvokeAsync("JoinGroup", groupId);

        // Một nửa kết nối của A, một nửa của B. Mỗi kết nối gửi lần lượt phần của mình; các kết nối chạy song song.
        var conns = await Task.WhenAll(Enumerable.Range(0, opt.Connections)
            .Select(i => ChatClient.ConnectAsync(opt.Gateway, i % 2 == 0 ? a : b)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var perConn = await Task.WhenAll(conns.Select(async (c, i) =>
        {
            var mine = new List<MessageDto>();
            for (var k = i; k < opt.Count; k += opt.Connections)
                mine.Add(await c.SendAsync(groupId, $"tin #{k}"));
            return mine;
        }));
        sw.Stop();
        foreach (var c in conns) await c.DisposeAsync();

        var sent = perConn.SelectMany(x => x).ToList();
        var seqs = sent.Select(m => m.SequenceNumber).OrderBy(s => s).ToList();
        Out.Info($"Gửi xong {sent.Count} tin trong {sw.ElapsedMilliseconds} ms (~{sent.Count * 1000 / Math.Max(1, sw.ElapsedMilliseconds)} tin/giây)");
        Out.Info($"seq nhỏ nhất = {seqs.First()}, lớn nhất = {seqs.Last()}");

        await Task.Delay(1000);
        List<MessageDto> received;
        lock (listener.Received) received = [.. listener.Received];
        // Tin đến người nghe theo thứ tự PHÁT, không nhất thiết theo seq: tin seq 7 có thể lưu xong trước tin seq 6.
        var outOfOrder = received.Zip(received.Skip(1)).Count(p => p.Second.SequenceNumber < p.First.SequenceNumber);

        var checks = new List<bool>
        {
            Out.Check($"Cả {opt.Count} lời gọi thành công", sent.Count == opt.Count, $"{sent.Count}"),
            Out.Check("Không có 2 tin trùng seq", seqs.Distinct().Count() == seqs.Count),
            Out.Check("Không thiếu số nào (liên tục 1..N)", seqs.First() == 1 && seqs.Last() == opt.Count),
            Out.Check($"Người nghe nhận đủ {opt.Count} tin", received.Count == opt.Count, $"{received.Count}")
        };
        Out.Info($"Số lần tin đến người nghe KHÔNG theo thứ tự seq: {outOfOrder} → client phải tự sắp theo sequenceNumber");

        Out.Info($"\nKiểm tra trong DB:");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select count(*), count(distinct sequence_number), min(sequence_number), max(sequence_number) from messages where group_id = '{groupId}'\"");
        return checks.All(x => x);
    }

    // Bước 4 – bộ đếm Redis hỏng: (1) mất key, (2) key bị lùi về số cũ, (3) mất key đúng lúc nhiều người đang gửi.
    // Script tự làm hỏng Redis bằng "docker exec redis redis-cli ...".
    public static async Task<bool> SeqLost(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo mất bộ đếm");
        var key = $"chat:seq:{groupId}";
        await using var conn = await ChatClient.ConnectAsync(opt.Gateway, a);
        for (var i = 1; i <= 3; i++) await conn.SendAsync(groupId, $"tin {i}");
        Out.Info($"Nhóm {groupId}: đã gửi 3 tin, {key} = {Redis($"GET {key}")}\n");
        var checks = new List<bool>();

        // (1) Mất key: không có EXISTS + khởi tạo từ MAX thì INCR sẽ trả 1 → trùng.
        Out.Info($"(1) redis-cli DEL {key} → {Redis($"DEL {key}")}");
        var m4 = await conn.SendAsync(groupId, "sau khi mất key");
        checks.Add(Out.Check("Tin tiếp theo có seq = 4 (khởi tạo lại từ MAX trong DB)", m4.SequenceNumber == 4, $"seq={m4.SequenceNumber}"));

        // (2) Key bị lùi (giống Redis khôi phục từ bản sao lưu cũ): EXISTS = có nên không phát hiện trước được.
        //     INCR → 2 (đã dùng) → unique index từ chối → đồng bộ lại → thử lại.
        Out.Info($"(2) redis-cli SET {key} 1 → {Redis($"SET {key} 1")}");
        var m5 = await conn.SendAsync(groupId, "sau khi bộ đếm bị lùi");
        checks.Add(Out.Check("Tin tiếp theo có seq = 5 (phát hiện trùng, đồng bộ lại, thử lại)", m5.SequenceNumber == 5, $"seq={m5.SequenceNumber}"));

        // (3) Mất key trong lúc 20 kết nối đang gửi cùng lúc (400 tin, đủ lâu để lệnh DEL rơi vào giữa chừng;
        //     "docker exec" mất vài trăm ms mới chạy).
        const int total = 400;
        var conns = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ChatClient.ConnectAsync(opt.Gateway, a)));
        var sending = Task.WhenAll(conns.Select(async c =>
        {
            var mine = new List<MessageDto>();
            for (var k = 0; k < total / 20; k++) mine.Add(await c.SendAsync(groupId, "song song"));
            return mine;
        }));
        await Task.Delay(100);
        Out.Info($"(3) Đang có 20 kết nối gửi {total} tin, redis-cli DEL {key} → {Redis($"DEL {key}")}");
        var parallel = (await sending).SelectMany(x => x).Select(m => m.SequenceNumber).ToList();
        foreach (var c in conns) await c.DisposeAsync();
        checks.Add(Out.Check($"{total} tin song song đều lưu được, không trùng seq",
            parallel.Count == total && parallel.Distinct().Count() == total, $"{parallel.Count} tin, {parallel.Distinct().Count()} seq khác nhau"));

        Out.Info($"\nXem log chat-service: các dòng \"Đồng bộ lại bộ đếm {key} ...\"");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"select count(*), count(distinct sequence_number), max(sequence_number) from messages where group_id = '{groupId}'\"");
        return checks.All(x => x);
    }

    static string Redis(string command) => Docker($"exec redis redis-cli {command}");

    static string Docker(string arguments)
    {
        // Bắt cả stderr (docker compose in tiến độ "Container ... Stopping" ra stderr) để không lẫn vào kết quả test.
        // Đọc 2 luồng song song: đọc lần lượt có thể treo khi luồng kia đầy bộ đệm.
        var psi = new System.Diagnostics.ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd().Trim();
        stderr.Wait();
        p.WaitForExit();
        return output;
    }

    // Kịch bản lỗi: group-service chết giữa chừng.
    // - Nhóm ĐÃ có trong cache Redis → vẫn chat được (không cần hỏi group-service).
    // - Nhóm CHƯA có trong cache → phải hỏi gRPC → không kiểm tra được → TỪ CHỐI (không cho qua bừa).
    // Bật lại group-service → nhóm chưa cache cũng gửi được ngay, không cần khởi động lại chat-service hay kết nối lại hub.
    public static async Task<bool> Down(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var cachedGroup = await api.CreateGroupAsync(a, "Demo group-service chết (có cache)");
        var uncachedGroup = await api.CreateGroupAsync(a, "Demo group-service chết (chưa cache)");
        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);

        // Tạo nhóm → group-service phát member-added (Owner) qua outbox, tới chat-service sau ~1 giây và XÓA cache.
        // Nạp cache ngay lúc này thì sự kiện đến muộn sẽ xóa mất → chờ sự kiện xử lý xong rồi mới nạp.
        await Task.Delay(3000);
        var checks = new List<bool>
        {
            // JoinGroup → cache miss → gọi gRPC → nạp cache group:members:{cachedGroup}.
            await Out.ExpectOk("group-service đang chạy → JoinGroup nhóm 1 (nạp cache)", () => connA.Hub.InvokeAsync("JoinGroup", cachedGroup)),
            Out.Check("Nhóm 1 đã có cache", Redis($"EXISTS group:members:{cachedGroup}") == "1")
        };
        Out.Info($"  cache: redis-cli SMEMBERS group:members:{cachedGroup} → {Redis($"SMEMBERS group:members:{cachedGroup}")}");
        Out.Info($"  nhóm 2 chưa ai hỏi: redis-cli EXISTS group:members:{uncachedGroup} → {Redis($"EXISTS group:members:{uncachedGroup}")}");

        Out.Warn("\n>>> Hãy TẮT group-service (Ctrl+C ở cửa sổ của nó). Script tự phát hiện...");
        await api.WaitGroupServiceAsync(a, up: false);
        Out.Info("Đã phát hiện group-service tắt (Gateway trả 502).");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        checks.Add(await Out.ExpectOk("Nhóm 1 (có cache) → SendMessage VẪN được", () => connA.SendAsync(cachedGroup, "lúc group-service chết")));
        Out.Info($"  mất {sw.ElapsedMilliseconds} ms (không gọi gRPC)");

        sw.Restart();
        checks.Add(await Out.ExpectHubError("Nhóm 2 (chưa cache) → SendMessage bị từ chối", () => connA.SendAsync(uncachedGroup, "lúc group-service chết")));
        // DeadlineExceeded (~3 s, đang chờ kết nối) hoặc Unavailable (nhanh hơn: gRPC client đã biết kết nối hỏng
        // và đang trong thời gian chờ "reconnect backoff" → từ chối ngay, không chờ hết deadline).
        Out.Info($"  mất {sw.ElapsedMilliseconds} ms");
        checks.Add(Out.Check("Kết nối hub vẫn còn", connA.Hub.State == HubConnectionState.Connected, connA.Hub.State.ToString()));

        Out.Warn("\n>>> Hãy BẬT LẠI group-service. Script tự phát hiện...");
        await api.WaitGroupServiceAsync(a, up: true);
        // gRPC client thử kết nối lại theo backoff tăng dần (1 s, 1.6 s, 2.5 s...) → có thể mất vài giây mới thông.
        checks.Add(await Out.Eventually("group-service sống lại → nhóm 2 SendMessage được, cùng kết nối hub cũ", expectOk: true,
            () => connA.SendAsync(uncachedGroup, "group-service đã sống lại"), timeoutMs: 30000));
        return checks.All(x => x);
    }

    // Bước 5 – cache thành viên: nạp khi cần, có TTL, bị xóa khi thêm/xóa thành viên (qua Kafka).
    public static async Task<bool> Cache(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo cache thành viên");
        var key = $"group:members:{groupId}";
        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b);
        Out.Info($"Nhóm {groupId}: Owner = {a.UserName} ({a.UserId}), {b.UserName} = {b.UserId}\n");
        var checks = new List<bool>();

        // 1. Chưa ai hỏi → chưa có cache. Gửi tin → cache miss → gRPC → nạp cache.
        checks.Add(Out.Check("Ban đầu chưa có cache", Redis($"EXISTS {key}") == "0"));
        await connA.SendAsync(groupId, "tin đầu tiên");
        var members = Redis($"SMEMBERS {key}");
        var ttl = int.Parse(Redis($"TTL {key}"));
        Out.Info($"Sau khi gửi tin: SMEMBERS = {members.Replace('\n', ' ')}, TTL = {ttl} giây");
        checks.Add(Out.Check("Cache có Owner, TTL ~600 giây", members.Contains(a.UserId.ToString()) && ttl is > 500 and <= 600));

        // 2. Thêm B qua REST → group-service phát member-added → chat-service xóa cache → B gửi được.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await api.AddMemberAsync(a, groupId, b.UserId);
        var gone = await WaitUntil(() => Redis($"EXISTS {key}") == "0");
        checks.Add(Out.Check("Thêm thành viên → cache bị xóa", gone, $"sau {sw.ElapsedMilliseconds} ms"));
        checks.Add(await Out.ExpectOk($"{b.UserName} gửi tin được", () => connB.SendAsync(groupId, "mình mới vào")));
        checks.Add(Out.Check($"Cache nạp lại có cả {b.UserName}", Redis($"SMEMBERS {key}").Contains(b.UserId.ToString())));

        // 3. Xóa B → member-removed → cache bị xóa → B không gửi được nữa.
        sw.Restart();
        await api.RemoveMemberAsync(a, groupId, b.UserId);
        gone = await WaitUntil(() => Redis($"EXISTS {key}") == "0");
        checks.Add(Out.Check("Xóa thành viên → cache bị xóa", gone, $"sau {sw.ElapsedMilliseconds} ms"));
        checks.Add(await Out.ExpectHubError($"{b.UserName} (đã bị xóa) gửi tin → bị từ chối", () => connB.SendAsync(groupId, "còn gửi được không?")));

        Out.Info($"\nXem log chat-service: \"Nạp cache {key} từ gRPC\" và \"Xóa cache thành viên {key}\".");
        return checks.All(x => x);
    }

    // Bước 6 – presence: A ngồi trong phòng; B mở 2 kết nối, đóng lần lượt.
    // Đóng kết nối thứ nhất → A KHÔNG nhận offline (B vẫn còn tab khác). Đóng kết nối cuối → A nhận offline.
    public static async Task<bool> Presence(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo presence");
        await api.AddMemberAsync(a, groupId, b.UserId);
        var keyB = $"presence:{b.UserId}";
        Out.Info($"Nhóm {groupId}: {a.UserName} + {b.UserName} ({b.UserId})\n");
        var checks = new List<bool>();

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a);
        var onlineSeenByA = await connA.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        Out.Info($"{a.UserName} JoinGroup → online: {Names(onlineSeenByA, a, b)}");
        checks.Add(Out.Check($"Lúc đầu chỉ {a.UserName} online", onlineSeenByA.Count == 1 && onlineSeenByA[0] == a.UserId));

        // B mở kết nối 1 → vào phòng → A nhận "B online"; JoinGroup của B trả về cả A và B.
        var b1 = await ChatClient.ConnectAsync(opt.Gateway, b);
        var onlineSeenByB = await b1.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        Out.Info($"{b.UserName} (kết nối 1) JoinGroup → online: {Names(onlineSeenByB, a, b)}");
        checks.Add(Out.Check($"{b.UserName} thấy cả 2 online", onlineSeenByB.Count == 2));
        checks.Add(Out.Check($"{a.UserName} nhận UserPresenceChanged({b.UserName}, true)",
            await WaitUntil(() => HasEvent(connA, b.UserId, true), 3000)));

        // B mở kết nối 2 (tab thứ hai).
        var b2 = await ChatClient.ConnectAsync(opt.Gateway, b);
        await b2.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        Out.Info($"{b.UserName} mở kết nối 2 → redis-cli SCARD {keyB} = {Redis($"SCARD {keyB}")}");
        checks.Add(Out.Check($"{keyB} có 2 ConnectionId", Redis($"SCARD {keyB}") == "2"));

        // Đóng kết nối 1 → vẫn còn kết nối 2 → KHÔNG báo offline.
        await b1.DisposeAsync();
        await Task.Delay(1000);
        checks.Add(Out.Check($"Đóng kết nối 1 → {a.UserName} KHÔNG nhận offline", !HasEvent(connA, b.UserId, false)));
        checks.Add(Out.Check($"{keyB} còn 1 ConnectionId", Redis($"SCARD {keyB}") == "1"));

        // Đóng kết nối cuối → offline.
        await b2.DisposeAsync();
        checks.Add(Out.Check($"Đóng kết nối cuối → {a.UserName} nhận UserPresenceChanged({b.UserName}, false)",
            await WaitUntil(() => HasEvent(connA, b.UserId, false), 3000)));
        checks.Add(Out.Check($"{keyB} bị xóa (set rỗng)", Redis($"EXISTS {keyB}") == "0"));

        var after = await connA.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        checks.Add(Out.Check($"{a.UserName} JoinGroup lại → chỉ còn {a.UserName} online", after.Count == 1 && after[0] == a.UserId,
            Names(after, a, b)));

        Out.Info($"\nDemo lỗi (kết nối \"ma\"): giữ một kết nối của {b.UserName}, giết tiến trình chat-service, rồi:");
        Out.Info($"  docker exec redis redis-cli SMEMBERS {keyB}   → ConnectionId cũ vẫn còn, {b.UserName} bị coi là online mãi");
        return checks.All(x => x);

        static bool HasEvent(ChatClient c, Guid userId, bool online)
        {
            lock (c.Presence) return c.Presence.Contains((userId, online));
        }
        static string Names(List<Guid> ids, LoggedInUser a, LoggedInUser b) =>
            string.Join(", ", ids.Select(id => id == a.UserId ? a.UserName : id == b.UserId ? b.UserName : id.ToString()));
    }

    // Phần 8 – 2 bản chat-service. A nối THẲNG bản 1 (--a), B nối THẲNG bản 2 (--b), không qua Gateway/Nginx
    // → chắc chắn 2 người nằm ở 2 tiến trình khác nhau. (REST đăng nhập, tạo nhóm vẫn đi qua Gateway.)
    // - Trạng thái để ở Redis (bộ đếm seq, presence) → 2 bản tự dùng chung → luôn đúng.
    // - Phát qua Clients.Group / OthersInGroup (ReceiveMessage, UserPresenceChanged): phòng SignalR nằm trong RAM
    //   của từng bản → chỉ tới được bản kia khi có Redis Backplane (Bước 4). Chưa có → các dòng "giữa 2 bản" SAI.
    public static async Task<bool> Backplane(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo backplane");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: {a.UserName} + {b.UserName}");

        await using var connA = await ChatClient.ConnectAsync(opt.InstanceA, a);
        await using var connB = await ChatClient.ConnectAsync(opt.InstanceB, b);
        Out.Info($"{a.UserName} → bản 1 ({opt.InstanceA}), {b.UserName} → bản 2 ({opt.InstanceB})");

        await connA.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        var onlineSeenByB = await connB.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        var m1 = await connA.SendAsync(groupId, "Gửi từ bản 1");
        var m2 = await connB.SendAsync(groupId, "Gửi từ bản 2");
        await Task.Delay(1000); // chờ ReceiveMessage / UserPresenceChanged tới nơi (nếu có)

        Out.Info("\nDùng chung qua Redis (không cần backplane):");
        var checks = new List<bool>
        {
            Out.Check($"JoinGroup của {b.UserName} (bản 2) thấy {a.UserName} (bản 1) online",
                onlineSeenByB.Contains(a.UserId), $"{onlineSeenByB.Count} người online"),
            Out.Check("seq do Redis INCR cấp, liên tiếp dù 2 tin ở 2 bản", m2.SequenceNumber == m1.SequenceNumber + 1,
                $"{m1.SequenceNumber}, {m2.SequenceNumber}"),
            Out.Check($"{a.UserName} nhận tin của chính mình (cùng bản 1)", Has(connA, m1.Id)),
            Out.Check($"{b.UserName} nhận tin của chính mình (cùng bản 2)", Has(connB, m2.Id)),
        };

        Out.Info("\nPhát giữa 2 bản (cần Redis Backplane):");
        var cross = new List<bool>
        {
            Out.Check($"{b.UserName} (bản 2) nhận tin {a.UserName} gửi ở bản 1", Has(connB, m1.Id)),
            Out.Check($"{a.UserName} (bản 1) nhận tin {b.UserName} gửi ở bản 2", Has(connA, m2.Id)),
            Out.Check($"{a.UserName} (bản 1) nhận UserPresenceChanged({b.UserName}, true) phát từ bản 2",
                HasPresence(connA, b.UserId, true)),
        };
        if (!cross.All(x => x))
            Out.Warn("→ Mỗi bản chỉ phát cho các kết nối nằm trong RAM của chính nó. Tin vẫn được LƯU (xem DB), chỉ không được ĐẨY sang bản kia.");

        Out.Info($"\nTin vẫn có trong DB: docker exec postgres psql -U chatapp -d chat_db -c \"select sequence_number, sender_name, content from messages where group_id = '{groupId}' order by sequence_number\"");
        Out.Info("Log từng bản:       docker compose logs chat-service-1 | Select-String \"vào phòng\"   (tương tự chat-service-2)");
        return checks.Concat(cross).All(x => x);

        static bool Has(ChatClient c, Guid messageId)
        {
            lock (c.Received) return c.Received.Any(m => m.Id == messageId);
        }
        static bool HasPresence(ChatClient c, Guid userId, bool online)
        {
            lock (c.Presence) return c.Presence.Contains((userId, online));
        }
    }

    // Phần 8 – Nginx chia tải. Mọi request đi Gateway (5000) → Nginx (5080) → một trong 2 bản chat-service.
    // (1) REST: 20 lần GET lịch sử, đọc header X-Chat-Upstream (Nginx gắn vào) → mỗi bản xử lý bao nhiêu.
    // (2) Hub BỎ negotiate: 10 kết nối → đếm dòng "User ... kết nối" trong log từng bản → cả 10 được, chia cho 2 bản.
    // (3) Hub CÓ negotiate (mặc định của SignalR): negotiate và mở WebSocket là 2 request riêng → round-robin đưa
    //     chúng tới 2 bản khác nhau → bản nhận WebSocket không biết connectionToken do bản kia cấp → 404.
    public static async Task<bool> Lb(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo load balancing");
        var names = InstanceNames();
        Out.Info($"Nhóm {groupId}. Bản chat-service: {string.Join(", ", names.Select(n => $"{n.Value} = {n.Key}"))}\n");
        var checks = new List<bool>();

        // (1) REST
        var rest = new SortedDictionary<string, int>();
        for (var i = 0; i < 20; i++)
        {
            var upstream = await api.GetUpstreamAsync(a, $"/api/chat/groups/{groupId}/messages?limit=1");
            var name = upstream is null ? "(không có header)" : names.GetValueOrDefault(upstream, upstream);
            rest[name] = rest.GetValueOrDefault(name) + 1;
        }
        Out.Info($"(1) 20 request REST: {string.Join(", ", rest.Select(r => $"{r.Key} = {r.Value}"))}");
        checks.Add(Out.Check("Cả 2 bản đều nhận request REST", names.Values.All(rest.ContainsKey)));

        // (2) Hub bỏ negotiate. Đếm dòng "User <id> kết nối" trong log từng bản TRƯỚC và SAU khi mở 10 kết nối → lấy hiệu
        // (không lọc theo thời gian: dễ đếm lẫn kết nối của lần chạy trước, và đồng hồ Windows/Docker có thể lệch).
        int CountConnects(string container) =>
            Docker($"logs {container}").Split('\n').Count(l => l.Contains($"User {a.UserId} kết nối"));
        var before = names.Values.ToDictionary(n => n, CountConnects);
        var conns = new List<ChatClient>();
        for (var i = 0; i < 10; i++)
            conns.Add(await ChatClient.ConnectAsync(opt.Gateway, a, negotiate: false));
        await Task.Delay(500);
        var perInstance = names.Values.ToDictionary(n => n, n => CountConnects(n) - before[n]);
        foreach (var c in conns) await c.DisposeAsync();
        Out.Info($"\n(2) 10 kết nối hub BỎ negotiate: {string.Join(", ", perInstance.Select(p => $"{p.Key} = {p.Value}"))}");
        checks.Add(Out.Check("Cả 10 kết nối thành công, chia cho cả 2 bản",
            perInstance.Values.Sum() == 10 && perInstance.Values.All(v => v > 0), $"tổng {perInstance.Values.Sum()}"));

        // (3) Hub có negotiate
        int ok = 0, failed = 0;
        string? lastError = null;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                await using var c = await ChatClient.ConnectAsync(opt.Gateway, a, negotiate: true);
                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                lastError = ex.Message;
            }
        }
        Out.Info($"\n(3) 10 kết nối hub CÓ negotiate: {ok} thành công, {failed} lỗi");
        if (lastError is not null) Out.Info($"    lỗi: {lastError}");
        checks.Add(Out.Check("Có negotiate sau round-robin → kết nối bị lỗi", failed > 0));

        Out.Info("\nXem Nginx chia request: docker compose logs nginx --tail 30");
        return checks.All(x => x);

        // "172.21.0.7:8080" → "chat-service-1" (Nginx chỉ biết IP; hỏi Docker tên container của từng IP).
        static Dictionary<string, string> InstanceNames() =>
            Docker("inspect -f \"{{.Name}} {{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}\" chat-service-1 chat-service-2")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().Split(' '))
                .ToDictionary(p => $"{p[1]}:8080", p => p[0].TrimStart('/'));
    }

    // Phần 8 – Kịch bản lỗi: một bản chat-service chết giữa lúc đang chat.
    // A và B nối qua Gateway → Nginx, bật WithAutomaticReconnect. A gửi 1 tin/giây. Giây thứ 10 script tắt bản đang giữ
    // kết nối của A (--action stop: SIGTERM, tắt đúng cách; kill: SIGKILL, chết đột ngột), giây thứ 25 bật lại.
    // - Kết nối đứt → client tự nối lại → Nginx đưa sang bản còn sống → Reconnected → gọi lại JoinGroup (ConnectionId mới).
    // - Tin gửi lúc đang mất kết nối bị lỗi → gửi lại với CÙNG messageId (idempotency) ở giây sau → không trùng.
    // - Tin B lỡ trong lúc B mất kết nối (ReceiveMessage không được lưu lại để gửi bù) → lấy bù bằng REST lịch sử.
    public static async Task<bool> Failover(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo failover");
        await api.AddMemberAsync(a, groupId, b.UserId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Log(string s) => Out.Info($"[{sw.Elapsed.TotalSeconds,5:0.0}s] {s}");

        string[] instances = ["chat-service-1", "chat-service-2"];
        int CountConnects(string container, Guid userId) =>
            Docker($"logs {container}").Split('\n').Count(l => l.Contains($"User {userId} kết nối"));
        var before = instances.ToDictionary(i => i, i => CountConnects(i, a.UserId));

        await using var connA = await ChatClient.ConnectAsync(opt.Gateway, a, reconnect: true);
        await using var connB = await ChatClient.ConnectAsync(opt.Gateway, b, reconnect: true);
        await Task.Delay(300);
        // Bản nào vừa ghi log "User <A> kết nối" thì đang giữ kết nối của A.
        var target = instances.FirstOrDefault(i => CountConnects(i, a.UserId) > before[i]) ?? instances[0];
        Out.Info($"Nhóm {groupId}. {a.UserName} đang nối vào {target} → sẽ {opt.Action} bản này.\n");

        var reconnects = new List<string>();
        foreach (var (conn, user) in new[] { (connA, a), (connB, b) })
        {
            conn.Hub.Reconnecting += ex => { Log($"{user.UserName}: MẤT kết nối ({ex?.Message ?? "server đóng"}) → đang thử nối lại"); return Task.CompletedTask; };
            conn.Hub.Reconnected += async _ =>
            {
                // Kết nối mới (ConnectionId mới, có thể ở bản khác) chưa ở trong phòng nào → phải vào lại phòng.
                lock (reconnects) reconnects.Add(user.UserName);
                await conn.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
                Log($"{user.UserName}: ĐÃ nối lại và JoinGroup lại");
            };
        }
        await connA.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);
        await connB.Hub.InvokeAsync<List<Guid>>("JoinGroup", groupId);

        var acked = new List<MessageDto>();
        int failedSends = 0;
        Guid? pendingId = null; // tin chưa chắc đã gửi được → lần sau gửi lại với CÙNG Id
        for (var i = 1; i <= opt.Seconds; i++)
        {
            // Chạy lệnh docker song song (Task.Run): stop có thể mất vài giây, trong lúc đó A vẫn tiếp tục gửi.
            if (i == 10 && opt.Action != "none")
            {
                Log($">>> docker compose {opt.Action} {target}");
                _ = Task.Run(() => Docker($"compose {opt.Action} {target}"));
            }
            if (i == 25 && opt.Action != "none")
            {
                Log($">>> docker compose start {target}");
                _ = Task.Run(() => Docker($"compose start {target}"));
            }

            var id = pendingId ?? Guid.CreateVersion7();
            try
            {
                var m = await connA.SendAsync(groupId, $"Tin lúc {i}s", id).WaitAsync(TimeSpan.FromSeconds(5));
                acked.Add(m);
                if (pendingId is not null) Log($"gửi lại {id.ToString()[^6..]} thành công → seq {m.SequenceNumber}");
                pendingId = null;
            }
            catch (Exception ex)
            {
                failedSends++;
                pendingId = id;
                Log($"gửi lỗi ({ex.GetType().Name}) → giây sau gửi lại cùng messageId {id.ToString()[^6..]}");
            }
            await Task.Delay(1000);
        }
        await Task.Delay(1000);

        // Ghép tin đẩy realtime với lịch sử REST (giống Angular: kết nối lại thì tải lại lịch sử).
        HashSet<Guid> realtime;
        lock (connB.Received) realtime = connB.Received.Select(m => m.Id).ToHashSet();
        List<string> presenceB;
        lock (connB.Presence) presenceB = connB.Presence.Where(p => p.UserId == a.UserId).Select(p => p.IsOnline ? "online" : "offline").ToList();
        var history = await api.GetMessagesAsync(b, $"/api/chat/groups/{groupId}/messages?limit=100");
        var historyIds = history.Select(m => m.Id).ToHashSet();
        var missedRealtime = acked.Count(m => !realtime.Contains(m.Id));
        Out.Info($"\n{a.UserName} gửi thành công {acked.Count} tin, {failedSends} lần gửi lỗi (đã gửi lại).");
        Out.Info($"{b.UserName} nhận realtime {acked.Count - missedRealtime}/{acked.Count}; lỡ {missedRealtime} tin lúc mất kết nối → lấy bù qua REST.");
        Out.Info($"{b.UserName} thấy {a.UserName}: {string.Join(" → ", presenceB)}");

        var checks = new List<bool>
        {
            Out.Check($"{a.UserName} tự nối lại sau khi {target} bị tắt", reconnects.Contains(a.UserName),
                $"nối lại: {string.Join(", ", reconnects)}"),
            Out.Check("Gửi được tiếp sau khi nối lại (tin cuối thành công)", pendingId is null),
            Out.Check("Mọi tin đã gửi thành công đều có trong DB", acked.All(m => historyIds.Contains(m.Id)),
                $"{history.Count} tin trong lịch sử"),
            Out.Check("Gửi lại cùng messageId không sinh tin trùng", history.Count == acked.Count
                && history.Select(m => m.SequenceNumber).Distinct().Count() == history.Count),
            Out.Check($"{b.UserName} có đủ tin (realtime + lịch sử)", acked.All(m => realtime.Contains(m.Id) || historyIds.Contains(m.Id))),
        };

        // Kết nối "ma": bản bị giết không kịp chạy OnDisconnectedAsync → ConnectionId cũ của A còn trong presence.
        var presenceKey = $"presence:{a.UserId}";
        Out.Info($"\n{presenceKey} còn {Redis($"SCARD {presenceKey}")} ConnectionId (A đang có 1 kết nối thật)"
            + (opt.Action == "kill" ? " → thừa = kết nối \"ma\" do kill" : ""));
        Out.Info("Consumer Kafka:  docker compose logs chat-service-1 chat-service-2 | Select-String \"được giao\"");
        return checks.All(x => x);
    }

    // Bước 7 – REST lịch sử: 120 tin, lật trang 50 → 50 → 20 → rỗng bằng beforeSeq.
    // Giữa trang 1 và 2 có 5 tin MỚI chen vào → trang sau vẫn đúng chỗ (keyset), không lặp, không sót.
    public static async Task<bool> History(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo lịch sử");
        var url = $"/api/chat/groups/{groupId}/messages";
        await using var conn = await ChatClient.ConnectAsync(opt.Gateway, a);
        for (var i = 1; i <= 120; i++) await conn.SendAsync(groupId, $"tin {i}");
        Out.Info($"Nhóm {groupId}: đã gửi 120 tin (seq 1..120)\n");
        var checks = new List<bool>();

        var page1 = await api.GetMessagesAsync(a, url);
        Out.Info($"GET {url}                → {Range(page1)}");
        checks.Add(Out.Check("Trang 1 = 50 tin mới nhất, cũ → mới", page1.Count == 50 && page1[0].SequenceNumber == 71 && page1[^1].SequenceNumber == 120));

        for (var i = 1; i <= 5; i++) await conn.SendAsync(groupId, $"tin mới chen vào {i}");
        Out.Info("(5 tin mới chen vào: seq 121..125)");

        var page2 = await api.GetMessagesAsync(a, $"{url}?beforeSeq={page1[0].SequenceNumber}");
        Out.Info($"GET ...?beforeSeq={page1[0].SequenceNumber,-4}         → {Range(page2)}");
        var page3 = await api.GetMessagesAsync(a, $"{url}?beforeSeq={page2[0].SequenceNumber}");
        Out.Info($"GET ...?beforeSeq={page2[0].SequenceNumber,-4}         → {Range(page3)}");
        var page4 = await api.GetMessagesAsync(a, $"{url}?beforeSeq={page3[0].SequenceNumber}");
        Out.Info($"GET ...?beforeSeq={page3[0].SequenceNumber,-4}         → {Range(page4)}");

        var all = page3.Concat(page2).Concat(page1).Select(m => m.SequenceNumber).ToList();
        checks.Add(Out.Check("Trang 2 = 21..70 dù có tin mới chen vào", page2.Count == 50 && page2[0].SequenceNumber == 21 && page2[^1].SequenceNumber == 70));
        checks.Add(Out.Check("Trang 3 = 20 tin cuối (1..20), trang 4 rỗng = hết", page3.Count == 20 && page4.Count == 0));
        checks.Add(Out.Check("Ghép 3 trang = 1..120, không lặp, không sót", all.SequenceEqual(Enumerable.Range(1, 120).Select(x => (long)x))));

        var small = await api.GetMessagesAsync(a, $"{url}?limit=3");
        checks.Add(Out.Check("limit=3 → 3 tin mới nhất (123..125)", small.Select(m => m.SequenceNumber).SequenceEqual([123L, 124L, 125L])));

        // Lỗi
        checks.Add(Out.Check("limit=0 → 400", await api.GetStatusAsync(a, $"{url}?limit=0") == 400));
        checks.Add(Out.Check("limit=101 → 400", await api.GetStatusAsync(a, $"{url}?limit=101") == 400));
        checks.Add(Out.Check("groupId không phải GUID → 404", await api.GetStatusAsync(a, "/api/chat/groups/abc/messages") == 404));
        checks.Add(Out.Check("Không token → 401", await api.GetStatusAsync(null, url) == 401));
        var c = await api.TryLoginAsync(opt.Outsider, opt.Password);
        if (c is not null)
            checks.Add(Out.Check($"{c.UserName} (ngoài nhóm) → 403", await api.GetStatusAsync(c, url) == 403));

        Out.Info($"\nXem kế hoạch truy vấn (dùng index, không quét cả bảng):");
        Out.Info($"  docker exec postgres psql -U chatapp -d chat_db -c \"explain analyze select * from messages where group_id = '{groupId}' and sequence_number < 71 order by sequence_number desc limit 50\"");
        return checks.All(x => x);

        static string Range(List<MessageDto> page) =>
            page.Count == 0 ? "[] (hết tin)" : $"{page.Count} tin, seq {page[0].SequenceNumber}..{page[^1].SequenceNumber}";
    }

    // Phần 9 – member-added và member-removed là 2 TOPIC khác nhau → Kafka không bảo đảm thứ tự giữa chúng.
    // (1) Thứ tự đúng (thêm/xóa thật qua REST) → bản sao trong notification_db đổi theo.
    // (2) Đảo thứ tự: tự đẩy thẳng lên Kafka "xóa B" (mới, t2) TRƯỚC rồi "thêm B" (cũ, t1 < t2) SAU,
    //     giống như sự kiện "thêm" bị chậm trên đường. Sự thật ở group-service: B KHÔNG còn là thành viên.
    // (3) Gửi lại y nguyên 2 sự kiện đó (trùng, at-least-once) → không đổi.
    // (4) "Thêm B" MỚI (t3 > t2) → B là thành viên trở lại (thêm lại sau khi bị xóa vẫn được).
    // (5) Hai sự kiện CÙNG occurredAt → phân xử bằng eventId; đẩy theo 2 thứ tự ngược nhau → cùng một kết quả.
    // Cuối cùng đẩy "xóa B" mới nhất để bản sao khớp lại group-service (B không phải thành viên thật).
    public static async Task<bool> Reorder(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo đảo thứ tự sự kiện");
        Out.Info($"Nhóm {groupId}: Owner = {a.UserName}, {b.UserName} = {b.UserId}\n");
        var checks = new List<bool>();

        Out.Info("(1) Thứ tự đúng – thêm rồi xóa qua REST:");
        checks.Add(Out.Check($"Owner {a.UserName} là thành viên trong bản sao", await WaitUntil(() => State(groupId, a.UserId) == "t")));
        await api.AddMemberAsync(a, groupId, b.UserId);
        checks.Add(Out.Check($"Thêm {b.UserName} → is_member = true", await WaitUntil(() => State(groupId, b.UserId) == "t")));
        await api.RemoveMemberAsync(a, groupId, b.UserId);
        checks.Add(Out.Check($"Xóa {b.UserName} → is_member = false (tombstone, dòng vẫn còn)", await WaitUntil(() => State(groupId, b.UserId) == "f")));

        using var kafka = new Kafka(opt.Kafka);
        // Sau mỗi lần đẩy chờ một chút cho consumer xử lý xong → sự kiện đến ĐÚNG thứ tự mà kịch bản muốn.
        static Task Settle() => Task.Delay(1500);

        Out.Info("\n(2) Đảo thứ tự – removed(t2) đến TRƯỚC added(t1), t1 < t2:");
        var t2 = DateTimeOffset.UtcNow;
        var t1 = t2.AddSeconds(-30);
        Guid removedId = Guid.CreateVersion7(), addedId = Guid.CreateVersion7();
        await kafka.SendMemberRemovedAsync(groupId, b.UserId, t2, removedId);
        Out.Info($"  đã đẩy MemberRemoved({b.UserName}, occurredAt = {t2:HH:mm:ss})");
        await Settle();
        await kafka.SendMemberAddedAsync(groupId, b.UserId, t1, addedId);
        Out.Info($"  đã đẩy MemberAdded  ({b.UserName}, occurredAt = {t1:HH:mm:ss})  ← sự kiện CŨ đến muộn");
        await Settle();
        var groupServiceSays = await api.GetStatusAsync(b, $"/api/groups/{groupId}");
        Out.Info($"  group-service (nguồn sự thật): {b.UserName} xem chi tiết nhóm → {groupServiceSays}{(groupServiceSays == 403 ? " (không phải thành viên)" : "")}");
        checks.Add(Out.Check($"{b.UserName} KHÔNG là thành viên trong bản sao (khớp group-service)", State(groupId, b.UserId) == "f"));
        checks.Add(Out.Check($"{b.UserName} không có bộ đếm unread", !HasCounter(groupId, b.UserId)));

        Out.Info("\n(3) Gửi lại y nguyên 2 sự kiện trên (cùng eventId, cùng occurredAt), theo cả 2 thứ tự:");
        await kafka.SendMemberAddedAsync(groupId, b.UserId, t1, addedId);
        await kafka.SendMemberRemovedAsync(groupId, b.UserId, t2, removedId);
        await Settle();
        checks.Add(Out.Check("Không đổi: is_member = false", State(groupId, b.UserId) == "f"));

        Out.Info("\n(4) Thêm lại – added(t3), t3 > t2:");
        var t3 = DateTimeOffset.UtcNow;
        await kafka.SendMemberAddedAsync(groupId, b.UserId, t3);
        Out.Info($"  đã đẩy MemberAdded  ({b.UserName}, occurredAt = {t3:HH:mm:ss})");
        await Settle();
        checks.Add(Out.Check($"{b.UserName} là thành viên trở lại", State(groupId, b.UserId) == "t"));
        checks.Add(Out.Check($"{b.UserName} có lại bộ đếm unread", HasCounter(groupId, b.UserId)));

        Out.Info("\n(5) Cùng occurredAt – eventId lớn hơn thắng, thứ tự đến không quan trọng:");
        var tie = DateTimeOffset.UtcNow;
        // So eventId đúng cách PostgreSQL so uuid (từng byte theo thứ tự chuẩn = so chuỗi hex chữ thường).
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() }.OrderBy(g => g.ToString("N"), StringComparer.Ordinal).ToArray();
        Guid small = ids[0], big = ids[1];
        // 2 nhóm "giả" (không có trong group-service), mỗi nhóm nhận cùng cặp sự kiện theo một thứ tự.
        Guid groupX = Guid.CreateVersion7(), groupY = Guid.CreateVersion7();
        await kafka.SendMemberAddedAsync(groupX, b.UserId, tie, small);
        await Settle();
        await kafka.SendMemberRemovedAsync(groupX, b.UserId, tie, big);
        await kafka.SendMemberRemovedAsync(groupY, b.UserId, tie, big);
        await Settle();
        await kafka.SendMemberAddedAsync(groupY, b.UserId, tie, small);
        await Settle();
        Out.Info($"  added eventId = …{small.ToString()[^6..]}, removed eventId = …{big.ToString()[^6..]} (lớn hơn)");
        checks.Add(Out.Check("Nhóm X (added rồi removed) → is_member = false", State(groupX, b.UserId) == "f"));
        checks.Add(Out.Check("Nhóm Y (removed rồi added) → is_member = false", State(groupY, b.UserId) == "f"));

        // Dọn: (4) làm bản sao nói B là thành viên, trong khi group-service thì không → đẩy "xóa B" mới nhất.
        await kafka.SendMemberRemovedAsync(groupId, b.UserId, DateTimeOffset.UtcNow);
        await Settle();
        checks.Add(Out.Check("Dọn: bản sao khớp lại group-service", State(groupId, b.UserId) == "f"));

        Out.Info($"\nXem bản sao: docker exec postgres psql -U chatapp -d notification_db -c \"select user_id, is_member, last_event_at from group_member_snapshots where group_id = '{groupId}'\"");
        Out.Info("Xem log notification-service: các dòng \"Snapshot: áp ...\" và \"Snapshot: BỎ QUA ...\"");
        return checks.All(x => x);

        // "t" = thành viên, "f" = tombstone, "" = chưa có dòng.
        static string State(Guid groupId, Guid userId) =>
            Psql("notification_db", $"select is_member from group_member_snapshots where group_id = '{groupId}' and user_id = '{userId}'");

        static bool HasCounter(Guid groupId, Guid userId) =>
            Psql("notification_db", $"select count(*) from unread_counters where group_id = '{groupId}' and user_id = '{userId}'") == "1";
    }

    // Phần 9 – so bản sao thành viên của notification-service với dữ liệu gốc ở group_db (đọc chéo DB CHỈ để kiểm tra).
    // Dùng sau khi replay (reset offset về đầu): bản sao dựng lại từ sự kiện phải khớp nguồn sự thật.
    public static Task<bool> Snapshot(Api api, Options opt)
    {
        var truth = Rows("group_db", "select group_id || ':' || user_id from group_members");
        var copy = Rows("notification_db", "select group_id || ':' || user_id from group_member_snapshots where is_member");
        var counters = Rows("notification_db", "select group_id || ':' || user_id from unread_counters");
        var tombstones = Psql("notification_db", "select count(*) from group_member_snapshots where not is_member");

        // Nhóm tạo TRƯỚC Phần 5 (chưa có outbox/Kafka) không có sự kiện nào → replay không thể dựng lại.
        // Đó là giới hạn của dữ liệu cũ, không phải lỗi của consumer → tách riêng, không tính là SAI.
        var noEvents = Rows("group_db", "select g.id || ':' || m.user_id from groups g join group_members m on m.group_id = g.id " +
            "where not exists (select 1 from outbox_messages o where o.key = g.id::text)");
        var missingAll = truth.Except(copy).ToList();
        var missing = missingAll.Where(m => !noEvents.Contains(m)).ToList();
        var extra = copy.Except(truth).ToList();
        Out.Info($"group_db.group_members:                       {truth.Count} thành viên ({noEvents.Count} thuộc nhóm không có sự kiện nào trong outbox – trước Phần 5)");
        Out.Info($"notification_db.group_member_snapshots:       {copy.Count} thành viên (is_member), {tombstones} tombstone");
        Out.Info($"notification_db.unread_counters:              {counters.Count} bộ đếm");
        foreach (var m in missingAll.Except(missing).Take(5)) Out.Info($"  thiếu do không có sự kiện (trước Phần 5): {m}");
        foreach (var m in missing.Take(5)) Out.Warn($"  thiếu trong bản sao: {m}");
        foreach (var m in extra.Take(5)) Out.Warn($"  thừa trong bản sao (thành viên ma): {m}");

        var checks = new List<bool>
        {
            Out.Check("Không có thành viên ma (bản sao không thừa)", extra.Count == 0, $"thừa {extra.Count}"),
            Out.Check("Không thiếu thành viên nào CÓ sự kiện", missing.Count == 0, $"thiếu {missing.Count}"),
            Out.Check("Mỗi thành viên đúng một bộ đếm unread", counters.SetEquals(copy))
        };
        return Task.FromResult(checks.All(x => x));

        static HashSet<string> Rows(string db, string sql) =>
            Psql(db, sql).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    }

    // Phần 9 – số tin chưa đọc thời gian thực qua /hubs/notifications.
    // A gửi tin ở chat hub → chat-service lưu + outbox → Kafka chat.message-sent → notification-service +1 → UnreadCountChanged.
    // B mở 2 kết nối (2 tab) → cả 2 nhận; A (người gửi) không nhận gì; B đánh dấu đã đọc ở REST → cả 2 tab về 0.
    public static async Task<bool> Unread(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo unread");
        await api.AddMemberAsync(a, groupId, b.UserId);
        Out.Info($"Nhóm {groupId}: {a.UserName} (Owner) gửi, {b.UserName} nhận");
        var checks = new List<bool>();

        // member-added của B phải tới notification-service TRƯỚC tin đầu tiên, nếu không tin đó không được đếm cho B
        // (2 topic khác nhau, xem giới hạn eventual consistency) → chờ B có bộ đếm (GET /unread thấy nhóm).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UnreadDto? start = null;
        await WaitUntilAsync(async () => (start = await api.GetUnreadAsync(b, groupId)) is not null);
        checks.Add(Out.Check($"{b.UserName} có bộ đếm của nhóm (member-added đã tới notification)", start is { UnreadCount: 0 },
            $"sau {sw.ElapsedMilliseconds} ms"));

        await using var tab1 = await NotificationClient.ConnectAsync(opt.Gateway, b);
        await using var tab2 = await NotificationClient.ConnectAsync(opt.Gateway, b);
        await using var notifA = await NotificationClient.ConnectAsync(opt.Gateway, a);
        await using var chatA = await ChatClient.ConnectAsync(opt.Gateway, a);
        Out.Info($"{b.UserName} mở 2 kết nối /hubs/notifications (2 tab); {a.UserName} mở 1 kết nối notification + 1 chat\n");

        Out.Info($"(1) {a.UserName} gửi 3 tin:");
        var sentAt = new List<DateTime>();
        long lastSeq = 0;
        for (var i = 1; i <= 3; i++)
        {
            sentAt.Add(DateTime.UtcNow);
            lastSeq = (await chatA.SendAsync(groupId, $"tin {i}")).SequenceNumber;
        }
        await WaitUntil(() => tab1.CountsFor(groupId).Count >= 3 && tab2.CountsFor(groupId).Count >= 3);
        var delays = tab1.Changes.Where(c => c.GroupId == groupId).Zip(sentAt, (c, s) => (c.At - s).TotalMilliseconds).ToList();
        checks.Add(Out.Check($"Tab 1 của {b.UserName} nhận UnreadCountChanged 1, 2, 3", tab1.CountsFor(groupId).SequenceEqual([1, 2, 3]),
            string.Join(", ", tab1.CountsFor(groupId))));
        checks.Add(Out.Check($"Tab 2 của {b.UserName} cũng nhận 1, 2, 3 (Clients.User → mọi kết nối)", tab2.CountsFor(groupId).SequenceEqual([1, 2, 3]),
            string.Join(", ", tab2.CountsFor(groupId))));
        Out.Info($"  độ trễ gửi tin → nhận thông báo: {string.Join(", ", delays.Select(d => $"{d:0} ms"))} (outbox quét mỗi ~1 giây + Kafka)");
        checks.Add(Out.Check($"REST GET /unread của {b.UserName} = 3 (khớp số vừa đẩy)", (await api.GetUnreadAsync(b, groupId))?.UnreadCount == 3));
        await Task.Delay(500);
        checks.Add(Out.Check($"{a.UserName} (người gửi) không nhận thông báo nào cho nhóm", notifA.CountsFor(groupId).Count == 0,
            $"nhận {notifA.CountsFor(groupId).Count}"));

        Out.Info($"\n(2) {b.UserName} mở nhóm ở tab 1 → POST read tới seq {lastSeq}:");
        var read = await api.MarkReadAsync(b, groupId, lastSeq);
        checks.Add(Out.Check("REST trả unread 0", read.UnreadCount == 0, $"mốc {read.LastReadSequence}"));
        await WaitUntil(() => tab2.CountsFor(groupId).Count >= 4);
        checks.Add(Out.Check("Tab 2 nhận UnreadCountChanged 0 (chấm đỏ tắt ở tab kia)", tab2.CountsFor(groupId).LastOrDefault(-1) == 0,
            string.Join(", ", tab2.CountsFor(groupId))));

        Out.Info($"\n(3) {a.UserName} gửi thêm 1 tin → bộ đếm tăng lại:");
        await chatA.SendAsync(groupId, "tin 4");
        await WaitUntil(() => tab1.CountsFor(groupId).Count >= 5);
        checks.Add(Out.Check($"{b.UserName} nhận UnreadCountChanged 1", tab1.CountsFor(groupId).LastOrDefault(-1) == 1,
            string.Join(", ", tab1.CountsFor(groupId))));

        Out.Info("\n(4) Kết nối /hubs/notifications không token:");
        try
        {
            await using var anon = new HubConnectionBuilder()
                .WithUrl($"{opt.Gateway}/hubs/notifications", o => { o.Transports = HttpTransportType.WebSockets; o.SkipNegotiation = true; })
                .Build();
            await anon.StartAsync();
            checks.Add(Out.Check("Bị từ chối", false, "lại kết nối được!"));
        }
        catch (Exception ex)
        {
            // Bỏ negotiate → lỗi đến từ bước bắt tay WebSocket (Gateway trả 401 thay vì 101).
            checks.Add(Out.Check("Bị từ chối", ex.Message.Contains("401"), ex.Message.Split('\n')[0]));
        }

        Out.Info("\nXem log notification-service: \"Kết nối ... của user\", \"MessageSent seq ... +1 cho 1 người\", \"User ... đọc nhóm ...\"");
        return checks.All(x => x);
    }

    // Phần 9 – at-least-once: tự đẩy CÙNG MỘT MessageSent (cùng eventId) lên Kafka 3 lần, như khi OutboxPublisher
    // gửi lại vì chưa kịp ghi processed_at, hoặc consumer sập trước khi commit offset.
    // Idempotent Consumer BẬT → B chỉ +1. TẮT (Notification:IdempotentConsumer=false) → +3 = SAI (đúng như demo muốn thấy).
    public static async Task<bool> DupEvent(Api api, Options opt)
    {
        var a = await api.LoginAsync(opt.User, opt.Password);
        var b = await api.LoginAsync(opt.Other, opt.Password);
        var groupId = await api.CreateGroupAsync(a, "Demo sự kiện trùng");
        await api.AddMemberAsync(a, groupId, b.UserId);
        await WaitUntilAsync(async () => await api.GetUnreadAsync(b, groupId) is not null);
        Out.Info($"Nhóm {groupId}: {a.UserName} (Owner) + {b.UserName}");

        await using var tabB = await NotificationClient.ConnectAsync(opt.Gateway, b);
        using var kafka = new Kafka(opt.Kafka);

        var eventId = Guid.CreateVersion7();
        Out.Info($"\nĐẩy CÙNG MỘT MessageSent (eventId …{eventId.ToString()[^6..]}, seq 1) lên chat.message-sent 3 lần:");
        for (var i = 1; i <= 3; i++)
        {
            await kafka.SendMessageSentAsync(eventId, groupId, a.UserId, "(giả lập)", 1, "tin bị gửi trùng");
            Out.Info($"  lần {i}: đã đẩy");
        }
        // Chờ consumer xử lý xong cả 3 (sự kiện trùng không đẩy SignalR → không chờ được bằng hub, chờ cố định).
        await Task.Delay(3000);

        var unread = (await api.GetUnreadAsync(b, groupId))?.UnreadCount;
        var processed = Psql("notification_db", $"select count(*) from processed_events where event_id = '{eventId}'");
        Out.Info($"  {b.UserName}: unread = {unread}; UnreadCountChanged nhận được: [{string.Join(", ", tabB.CountsFor(groupId))}]; processed_events có {processed} dòng cho eventId này");

        var checks = new List<bool>
        {
            Out.Check($"{b.UserName} chỉ +1 (không phải +3)", unread == 1, $"unread = {unread}"),
            Out.Check("Chỉ đẩy UnreadCountChanged 1 lần", tabB.CountsFor(groupId).SequenceEqual([1]), string.Join(", ", tabB.CountsFor(groupId)))
        };
        if (unread == 3)
            Out.Warn("  → Đếm 3 lần: notification-service đang chạy với Notification:IdempotentConsumer = false (công tắc demo).");

        Out.Info("\nXem log notification-service: 2 dòng \"Bỏ qua sự kiện trùng ...\" (khi công tắc BẬT).");
        return checks.All(x => x);
    }

    static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs && !await condition())
            await Task.Delay(200);
    }

    // -t: chỉ in dữ liệu (không tiêu đề), -A: không căn lề → kết quả đọc thẳng được.
    static string Psql(string database, string sql) => Docker($"exec postgres psql -U chatapp -d {database} -tAc \"{sql}\"");

    static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return false;
    }

    static bool IsConsecutive(IEnumerable<long> seqs)
    {
        var list = seqs.ToList();
        return list.Zip(list.Skip(1)).All(p => p.Second == p.First + 1);
    }
}

// ---------------- Hạ tầng của script ----------------

record MessageDto(Guid Id, Guid GroupId, Guid SenderId, string SenderName, string Content, long SequenceNumber, DateTimeOffset CreatedAt);

record LoggedInUser(string UserName, Guid UserId, string Token);

// Một kết nối SignalR của một user, ghi lại mọi ReceiveMessage nhận được.
sealed class ChatClient : IAsyncDisposable
{
    public HubConnection Hub { get; }
    public List<MessageDto> Received { get; } = [];
    public List<(Guid UserId, bool IsOnline)> Presence { get; } = [];

    ChatClient(HubConnection hub) => Hub = hub;

    // Đặt từ cờ --negotiate. Mặc định false = bỏ negotiate (Phần 8).
    public static bool Negotiate { get; set; }

    // baseUrl: Gateway (mặc định) hoặc địa chỉ trực tiếp của một bản chat-service (chế độ backplane).
    // reconnect: bật WithAutomaticReconnect (thử lại sau 0, 2, 10, 30 giây) như Angular sẽ làm.
    public static async Task<ChatClient> ConnectAsync(string baseUrl, LoggedInUser user, bool? negotiate = null, bool reconnect = false)
    {
        var useNegotiate = negotiate ?? Negotiate;
        var builder = new HubConnectionBuilder();
        if (reconnect) builder.WithAutomaticReconnect();
        var hub = builder
            .WithUrl($"{baseUrl}/hubs/chat", o =>
            {
                // AccessTokenProvider: thư viện tự gắn token vào header Authorization (client .NET gắn được header
                // cả khi mở WebSocket; trình duyệt thì không → Angular dùng ?access_token=).
                o.AccessTokenProvider = () => Task.FromResult<string?>(user.Token);
                // Chỉ WebSocket: không thử SSE/long polling. Long polling = nhiều request HTTP nối tiếp,
                // round-robin sẽ rải chúng ra 2 bản → càng hỏng.
                o.Transports = HttpTransportType.WebSockets;
                // SkipNegotiation: bỏ request POST /negotiate, mở thẳng WebSocket → cả vòng đời kết nối chỉ có MỘT
                // request HTTP (Upgrade) → Nginx chọn bản nào thì kết nối ở luôn bản đó.
                o.SkipNegotiation = !useNegotiate;
            })
            .Build();
        var client = new ChatClient(hub);
        hub.On<MessageDto>("ReceiveMessage", m => { lock (client.Received) client.Received.Add(m); });
        hub.On<Guid, bool>("UserPresenceChanged", (u, online) => { lock (client.Presence) client.Presence.Add((u, online)); });
        await hub.StartAsync();
        return client;
    }

    public Task<MessageDto> SendAsync(Guid groupId, string content, Guid? messageId = null) =>
        Hub.InvokeAsync<MessageDto>("SendMessage", messageId ?? Guid.CreateVersion7(), groupId, content);

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}

// Một kết nối tới /hubs/notifications (Phần 9), ghi lại mọi UnreadCountChanged kèm thời điểm nhận.
sealed class NotificationClient : IAsyncDisposable
{
    public HubConnection Hub { get; }
    public List<(Guid GroupId, int Count, DateTime At)> Changes { get; } = [];

    NotificationClient(HubConnection hub) => Hub = hub;

    // Cùng cách kết nối với chat hub (bỏ negotiate, chỉ WebSocket) – Angular dùng chung một kiểu cho cả 2 hub.
    public static async Task<NotificationClient> ConnectAsync(string gateway, LoggedInUser user)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl($"{gateway}/hubs/notifications", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(user.Token);
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = !ChatClient.Negotiate;
            })
            .Build();
        var client = new NotificationClient(hub);
        hub.On<Guid, int>("UnreadCountChanged", (g, n) => { lock (client.Changes) client.Changes.Add((g, n, DateTime.UtcNow)); });
        await hub.StartAsync();
        return client;
    }

    public List<int> CountsFor(Guid groupId)
    {
        lock (Changes) return Changes.Where(c => c.GroupId == groupId).Select(c => c.Count).ToList();
    }

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}

record UnreadDto(Guid GroupId, int UnreadCount, long LastReadSequence);

sealed class Api(string gateway)
{
    readonly HttpClient _http = new() { BaseAddress = new Uri(gateway) };

    public async Task<LoggedInUser> LoginAsync(string userName, string password) =>
        await TryLoginAsync(userName, password)
        ?? throw new InvalidOperationException($"Đăng nhập '{userName}' thất bại (sai mật khẩu hoặc chưa đăng ký; dùng --user/--other/--password)");

    public async Task<LoggedInUser?> TryLoginAsync(string userName, string password)
    {
        var resp = await _http.PostAsJsonAsync("/api/auth/login", new { userName, password });
        if (!resp.IsSuccessStatusCode) return null;
        var token = (await resp.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        var me = await Send<UserDto>(HttpMethod.Get, "/api/users/me", token);
        return new LoggedInUser(userName, me.Id, token);
    }

    public async Task<Guid> CreateGroupAsync(LoggedInUser owner, string name) =>
        (await Send<IdDto>(HttpMethod.Post, "/api/groups", owner.Token, new { name })).Id;

    public async Task AddMemberAsync(LoggedInUser owner, Guid groupId, Guid userId)
    {
        var resp = await SendRaw(HttpMethod.Post, $"/api/groups/{groupId}/members", owner.Token, new { userId });
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Thêm thành viên {userId} → 404: user chưa có trong user_snapshots (đăng ký lại user sau Phần 5)");
        resp.EnsureSuccessStatusCode();
    }

    // GET qua Gateway, trả về header X-Chat-Upstream do Nginx gắn: request vừa được bản chat-service nào (IP:port) xử lý.
    public async Task<string?> GetUpstreamAsync(LoggedInUser user, string path)
    {
        var resp = await SendRaw(HttpMethod.Get, path, user.Token);
        resp.EnsureSuccessStatusCode();
        return resp.Headers.TryGetValues("X-Chat-Upstream", out var values) ? values.First() : null;
    }

    public Task<List<MessageDto>> GetMessagesAsync(LoggedInUser user, string path) =>
        Send<List<MessageDto>>(HttpMethod.Get, path, user.Token);

    // user = null → gửi không token.
    public async Task<int> GetStatusAsync(LoggedInUser? user, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (user is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        return (int)(await _http.SendAsync(req)).StatusCode;
    }

    public async Task<UnreadDto?> GetUnreadAsync(LoggedInUser user, Guid groupId) =>
        (await Send<List<UnreadDto>>(HttpMethod.Get, "/api/notifications/unread", user.Token)).FirstOrDefault(u => u.GroupId == groupId);

    public Task<UnreadDto> MarkReadAsync(LoggedInUser user, Guid groupId, long lastReadSequence) =>
        Send<UnreadDto>(HttpMethod.Post, $"/api/notifications/groups/{groupId}/read", user.Token, new { lastReadSequence });

    public async Task RemoveMemberAsync(LoggedInUser owner, Guid groupId, Guid userId) =>
        (await SendRaw(HttpMethod.Delete, $"/api/groups/{groupId}/members/{userId}", owner.Token)).EnsureSuccessStatusCode();

    public async Task DeleteGroupAsync(LoggedInUser owner, Guid groupId) =>
        (await SendRaw(HttpMethod.Delete, $"/api/groups/{groupId}", owner.Token)).EnsureSuccessStatusCode();

    // Hỏi Gateway mỗi giây tới khi group-service ở trạng thái mong muốn. 502 Bad Gateway = service phía sau không trả lời.
    // Phải có token thật: không token thì Gateway chặn 401 trước, không bao giờ tới group-service.
    public async Task WaitGroupServiceAsync(LoggedInUser user, bool up)
    {
        while (true)
        {
            var resp = await SendRaw(HttpMethod.Get, "/api/groups", user.Token);
            var isUp = resp.StatusCode != System.Net.HttpStatusCode.BadGateway;
            if (isUp == up) return;
            await Task.Delay(1000);
        }
    }

    async Task<T> Send<T>(HttpMethod method, string path, string token, object? body = null)
    {
        var resp = await SendRaw(method, path, token, body);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<T>())!;
    }

    Task<HttpResponseMessage> SendRaw(HttpMethod method, string path, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return _http.SendAsync(req);
    }

    record LoginResponse(string AccessToken);
    record UserDto(Guid Id);
    record IdDto(Guid Id);
}

// Tự đẩy sự kiện thẳng lên Kafka (giả làm group-service / chat-service) để tạo tình huống khó gặp:
// sự kiện đến sai thứ tự, sự kiện trùng. JSON tự viết theo đúng hợp đồng trong ChatApp.Contracts/Events
// (camelCase, có eventId, eventType, occurredAt) → script không phụ thuộc code của backend, bên nghe vẫn đọc được.
sealed class Kafka(string bootstrapServers) : IDisposable
{
    static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    readonly Confluent.Kafka.IProducer<string, string> _producer =
        new Confluent.Kafka.ProducerBuilder<string, string>(new Confluent.Kafka.ProducerConfig { BootstrapServers = bootstrapServers }).Build();

    public Task SendMemberAddedAsync(Guid groupId, Guid userId, DateTimeOffset occurredAt, Guid? eventId = null) =>
        SendAsync("group.member-added", groupId,
            new { eventId = eventId ?? Guid.CreateVersion7(), eventType = "MemberAdded", occurredAt, groupId, userId, role = "Member" });

    public Task SendMemberRemovedAsync(Guid groupId, Guid userId, DateTimeOffset occurredAt, Guid? eventId = null) =>
        SendAsync("group.member-removed", groupId,
            new { eventId = eventId ?? Guid.CreateVersion7(), eventType = "MemberRemoved", occurredAt, groupId, userId });

    // Giả làm chat-service phát chat.message-sent (đúng các field của Contracts/Events/MessageSent.cs).
    public Task SendMessageSentAsync(Guid eventId, Guid groupId, Guid senderId, string senderName, long sequenceNumber, string content) =>
        SendAsync("chat.message-sent", groupId,
            new { eventId, eventType = "MessageSent", occurredAt = DateTimeOffset.UtcNow, messageId = Guid.CreateVersion7(),
                  groupId, senderId, senderName, sequenceNumber, contentPreview = content });

    // Key = groupId giống group-service / chat-service → cùng partition với các sự kiện thật của nhóm.
    async Task SendAsync(string topic, Guid key, object evt) =>
        await _producer.ProduceAsync(topic, new Confluent.Kafka.Message<string, string>
        {
            Key = key.ToString(),
            Value = System.Text.Json.JsonSerializer.Serialize(evt, Json)
        });

    public void Dispose() => _producer.Dispose();
}

sealed record Options(string Mode, string User, string Other, string Outsider, string Password, string Gateway, int Count, int Connections,
    string InstanceA, string InstanceB, bool Negotiate, int Seconds, string Action, string Kafka)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        // Tham số đầu tiên (nếu không bắt đầu bằng --) là chế độ.
        var mode = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "basic";
        return new Options(mode, Get("user", "duong"), Get("other", "lan"), Get("outsider", "minh"),
            Get("password", "123456"), Get("gateway", "http://localhost:5000"),
            int.Parse(Get("count", "200")), int.Parse(Get("connections", "20")),
            Get("a", "http://localhost:5003"), Get("b", "http://localhost:5013"), args.Contains("--negotiate"),
            int.Parse(Get("seconds", "40")), Get("action", "stop"), Get("kafka", "localhost:9092"));
    }
}

static class Out
{
    public static void Info(string s) => Console.WriteLine(s);
    public static void Warn(string s) => Write(ConsoleColor.Yellow, s);
    public static void Fail(string s) => Write(ConsoleColor.Red, s);

    public static bool Check(string label, bool ok, string detail = "")
    {
        Write(ok ? ConsoleColor.Green : ConsoleColor.Red, $"  [{(ok ? "OK" : "SAI")}] {label}{(detail == "" ? "" : $"  ({detail})")}");
        return ok;
    }

    public static async Task<bool> ExpectOk(string label, Func<Task> action)
    {
        try { await action(); return Check(label, true); }
        catch (HubException ex) { return Check(label, false, ex.Message); }
    }

    // Lỗi do hub ném HubException → client nhận HubException kèm thông báo của server.
    // Thử lại tới khi đạt kết quả mong đợi (tối đa 10 giây), in ra mất bao lâu.
    // Dùng cho thay đổi lan truyền chậm (eventual consistency), vd thêm thành viên → chờ Kafka xóa cache.
    public static async Task<bool> Eventually(string label, bool expectOk, Func<Task> action, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string last = "";
        while (true)
        {
            bool ok;
            try { await action(); ok = true; last = "thành công"; }
            catch (HubException ex) { ok = false; last = ex.Message; }
            if (ok == expectOk) return Check(label, true, $"sau {sw.ElapsedMilliseconds} ms");
            if (sw.ElapsedMilliseconds > timeoutMs) return Check(label, false, $"quá {timeoutMs} ms, lần cuối: {last}");
            await Task.Delay(200);
        }
    }

    public static async Task<bool> ExpectHubError(string label, Func<Task> action)
    {
        try { await action(); return Check(label, false, "không bị từ chối!"); }
        catch (HubException ex) { return Check(label, true, ex.Message); }
    }

    public static void Result(bool ok) =>
        Write(ok ? ConsoleColor.Green : ConsoleColor.Red, ok ? "\nKẾT QUẢ: ĐÚNG" : "\nKẾT QUẢ: KHÔNG như mong đợi");

    static void Write(ConsoleColor color, string s)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(s);
        Console.ForegroundColor = old;
    }
}
