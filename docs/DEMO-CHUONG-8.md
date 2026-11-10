# Hướng dẫn demo chương 8 trên dự án hiện tại

Không cần chạy mỗi service trong một cửa sổ riêng. Identity, Group, Notification, Gateway là các tiến trình Windows được script mở nền; Chat là các Pod Kubernetes; kho dữ liệu và giám sát chạy Docker. Terminal chỉ là nơi nhập lệnh điều khiển. Hướng dẫn này dành cho mode Kubernetes, web nguồn 4200.

## 1. Chuẩn bị

Nếu dự án đang chạy khỏe thì không khởi động lại. Nếu vừa bật máy/Docker, chạy tại gốc và đợi READY:

```powershell
cd D:\Projects\ChatApp
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-resilience-demo.ps1 -SkipBuild
```

Không chạy thêm `docker compose up -d` toàn dự án hoặc mở thêm bản backend trên cùng cổng. Không tạo backup khi đang chủ động dừng service, thay Pod hoặc tạo tải. Chạy từng tình huống và khôi phục xong rồi mới sang tình huống tiếp theo.

Chuẩn bị hai trình duyệt/profile với hai tài khoản thường cùng tham gia một nhóm thử. Mở nhóm ở cả hai, gửi vài tin để kiểm tra ban đầu. Mở thêm:

| Trang | Địa chỉ |
|---|---|
| Web nguồn | http://localhost:4200 |
| Grafana | http://localhost:3000/d/chatapp-ch8 |
| Prometheus | http://localhost:9090 |
| Seq | http://localhost:5341 |

Grafana chọn khoảng 15 phút gần nhất, refresh 5s. Trong từng PowerShell dùng kubectl, khai báo lại biến sau vì biến không tự truyền sang cửa sổ khác:

```powershell
cd D:\Projects\ChatApp
$kc = 'D:\Projects\ChatApp\infra\backups\runtime\chapter8\kubeconfig'
kubectl --kubeconfig $kc -n chatapp get pods
kubectl --kubeconfig $kc -n chatapp get hpa
kubectl --kubeconfig $kc -n chatapp top pods
```

Ban đầu thường có hai Pod `1/1 Running`, HPA min 2/max 4, CPU có số. Nếu CPU hiện `<unknown>` kéo dài, kiểm tra Metrics Server trước khi demo HPA. Không dùng tên Pod trong ảnh cũ vì mỗi lần tạo lại tên sẽ thay đổi.

## 2. Health Monitoring: phát hiện một Pod chưa sẵn sàng

Trong một terminal quan sát:

```powershell
kubectl --kubeconfig $kc -n chatapp get pods -w
```

`Ctrl+C` ở lệnh `get ... -w` chỉ ngừng theo dõi, không dừng Pod.

Trong terminal điều khiển chọn một Pod đang Ready:

```powershell
$demoPod = Read-Host 'Nhap ten mot Chat Pod dang 1/1 Running'
kubectl --kubeconfig $kc -n chatapp exec $demoPod -- touch /tmp/chatapp-not-ready
```

Sau vài chu kỳ probe, Pod chuyển `0/1` nhưng vẫn Running; Grafana `Pod readiness` về 0. Kiểm tra EndpointSlice để thấy Pod này không còn là endpoint sẵn sàng:

```powershell
$demoSlices = kubectl --kubeconfig $kc -n chatapp get endpointslices -l kubernetes.io/service-name=chat -o json | ConvertFrom-Json
$demoSlices.items | ForEach-Object {
    $_.endpoints | Select-Object @{Name='Pod';Expression={$_.targetRef.name}}, @{Name='Ready';Expression={$_.conditions.ready}}
}
```

Tiến trình vẫn trả được liveness:

```powershell
kubectl --kubeconfig $kc get --raw "/api/v1/namespaces/chatapp/pods/${demoPod}:8080/proxy/health/live"
```

Kết quả có `Healthy`; số RESTARTS không tăng chỉ vì readiness thất bại. Kubernetes Service tránh đưa kết nối mới vào Pod này. WebSocket đã mở có thể vẫn hoạt động trên Pod đó; readiness không tự ngắt kết nối cũ. Prometheus `up` vẫn có thể bằng 1 vì endpoint metrics còn truy cập được, nên không đồng nhất `up` với readiness.

Khôi phục ngay sau khi quan sát:

```powershell
kubectl --kubeconfig $kc -n chatapp exec $demoPod -- rm -f /tmp/chatapp-not-ready
kubectl --kubeconfig $kc -n chatapp wait --for=condition=Ready pod/$demoPod --timeout=90s
```

Lời nói: “Readiness cho biết bản chạy có sẵn sàng nhận lưu lượng mới hay không. Bản chưa sẵn sàng được đưa ra khỏi tập phục vụ; tiến trình chưa cần bị khởi động lại.”

## 3. Load balancing và phục hồi khi mất một Chat Pod

Hai tài khoản vẫn mở cùng nhóm, gửi tin qua lại. Trong Grafana xem `Active SignalR connections`; Prometheus có thể truy vấn `chatapp_signalr_connections{service="chat"}` để xem từng instance. Nếu hai trình duyệt cùng rơi vào một Pod thì vẫn hợp lệ; Service không bảo đảm chia đều tuyệt đối vài kết nối.

Chọn Pod có kết nối nếu muốn thấy rõ client reconnect. Giữ terminal `get pods -w`, ở terminal điều khiển:

```powershell
$demoPod = Read-Host 'Nhap ten Pod Chat muon xoa de demo'
kubectl --kubeconfig $kc -n chatapp delete pod $demoPod --wait=false
```

Quan sát Pod cũ Terminating, một Pod tên mới được tạo rồi `1/1 Running`. Nếu trình duyệt nằm trên Pod bị xóa thì kết nối có thể gián đoạn ngắn rồi reconnect; nếu nằm trên Pod còn lại thì có thể không thấy gián đoạn. Sau khi kết nối lại, gửi tin hai chiều, tải lại trang để xem lịch sử vẫn còn.

`kubectl delete pod` mô phỏng mất một instance, có quy trình kết thúc của Kubernetes; không gọi đây là kiểm thử cắt điện cả máy. Không xóa cả Deployment, cả hai Pod hoặc volume. Pod mới thường có RESTARTS = 0: đó là Pod thay thế, khác việc restart container trong cùng Pod.

Lời nói: “Nginx chuyển tiếp tới Kubernetes Service; Service chọn Chat Pod sẵn sàng. Khi một Pod mất, các bản còn lại tiếp tục phục vụ và Deployment tạo bản thay thế. Client có kết nối bị mất sẽ nối lại, vào lại nhóm và lấy bù lịch sử. Dữ liệu tin nhắn nằm trong MongoDB dùng chung.”

Nếu cần chứng minh **liveness restart container khi process bị treo**, chạy riêng script sẵn có sau khi hai Pod khỏe:

```powershell
node .\scripts\k8s-probe-test.cjs
```

Script treo đúng process Chat, chờ liveness làm container restart rồi kiểm Ready, có nhánh dọn nếu lỗi. Quan sát RESTARTS tăng trong cùng Pod; kết quả ở `infra/backups/runtime/chapter8/probe-test-report.json`. Không chạy cùng lúc với HPA load hoặc suite khác.

## 4. Circuit Breaking và Log Management: tắt Group Service

Group là tiến trình Windows, không phải một trong hai Chat container. Script có sẵn chỉ dừng executable đúng dự án tại cổng 5002.

Trước khi dừng Group, chuẩn bị request đọc history để tạo đủ lỗi gRPC. Tại PowerShell điều khiển, đăng nhập bằng tài khoản thường đang là thành viên nhóm thử, nhập ID nhóm lấy ở cuối URL `/chat/<id>`:

```powershell
$demoCredential = Get-Credential -Message 'Tai khoan thuong thuoc nhom demo'
$demoLoginBody = @{
    userName = $demoCredential.UserName
    password = $demoCredential.GetNetworkCredential().Password
} | ConvertTo-Json
$demoLogin = Invoke-RestMethod 'http://localhost:4200/api/auth/login' -Method Post -ContentType 'application/json' -Body $demoLoginBody
$demoGroupId = Read-Host 'Nhap ID nhom demo, khong nhap ca URL'
$demoGroupGuid = [Guid]::Parse($demoGroupId)
Add-Type -AssemblyName System.Net.Http
[System.Net.ServicePointManager]::DefaultConnectionLimit = 48
$demoHttp = New-Object System.Net.Http.HttpClient
$demoHttp.Timeout = [TimeSpan]::FromSeconds(10)
$demoHttp.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $demoLogin.accessToken)
$demoHistoryUrl = "http://localhost:4200/api/chat/groups/$demoGroupGuid/messages?limit=10"
$demoBaseline = $demoHttp.GetAsync($demoHistoryUrl).GetAwaiter().GetResult()
$demoBaseline.StatusCode
$demoBaseline.Dispose()
```

Baseline phải `OK` (200). Nếu 401 thì đăng nhập lại; 403 thì tài khoản chưa có quyền nhóm; 404 thì kiểm ID. Không in token/mật khẩu hoặc chép chúng vào báo cáo.

Dừng Group:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\control-local-service.ps1 -Service Group -Action Stop
```

Trên web thử gửi tin: thao tác cần xác nhận quyền thành viên bị từ chối tạm thời. Tạo nhiều lượt đọc song song để dễ quan sát circuit (các lượt này không tạo tin mới):

```powershell
1..4 | ForEach-Object {
    $demoTasks = @(1..12 | ForEach-Object { $demoHttp.GetAsync($demoHistoryUrl) })
    foreach ($demoTask in $demoTasks) {
        try {
            $demoResponse = $demoTask.GetAwaiter().GetResult()
            Write-Host ('HTTP ' + [int]$demoResponse.StatusCode)
            $demoResponse.Dispose()
        } catch { Write-Host $_.Exception.GetBaseException().Message }
    }
    Start-Sleep -Milliseconds 500
}
```

Quan sát HTTP 503 cho history, Grafana panel `Circuit state: 0 closed / 1 open / 2 half-open`, Prometheus query `chatapp_circuit_state{dependency="group"}`. Circuit là riêng từng process/dependency, nên hai Pod có thể chuyển trạng thái khác lúc. Nếu chưa mở do tải bị chia hoặc chưa đủ lỗi trong cửa sổ đo, chạy lại block đọc khi Group vẫn đang dừng; không suy luận circuit đã mở chỉ từ một lần gửi thất bại.

Trong Seq http://localhost:5341, chọn log gần nhất, tìm:

```text
@Message like '%Circuit%'
```

Mở event `Circuit group changed to Open`; xem `Service`, `Instance`, `Dependency`, `CircuitState`. Có thể thấy log retry trước đó. Prometheus trang Alerts có `ChatAppCircuitOpen` khi điều kiện được giữ đủ lâu; log Seq lưu lại cả chuyển trạng thái nhanh mà biểu đồ có thể bỏ lỡ.

Các trạng thái:

- Closed = 0: cho RPC đi qua bình thường.
- Open = 1: tạm từ chối nhanh RPC tới dependency đang lỗi, không gửi dồn sang service đó.
- HalfOpen = 2: cho thử kiểm tra khả năng hồi phục; thành công thì Closed, thất bại thì Open lại.

Mặc định: tỷ lệ lỗi 50%, ít nhất sáu logical calls/15s trên mỗi process/dependency, mở khoảng 10s; tổng thời gian một lượt RPC tối đa 3s, retry một lần cho lỗi transport tạm thời. Không phải cứ lỗi một lần là mở circuit.

Bật Group lại ngay sau khi ghi nhận bằng chứng:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\control-local-service.ps1 -Service Group -Action Start
Invoke-RestMethod 'http://localhost:5002/health/ready'
```

Lệnh Start khởi động process, không chờ hoàn tất; nếu health chưa lên thì đợi vài giây rồi thử lại. Sau đó mở history/gửi tin tiếp để phát sinh lượt kiểm tra sau thời gian nghỉ của circuit. Quan sát Seq HalfOpen/Closed và history 200; không cần restart Chat. HalfOpen có thể quá ngắn để thấy rõ trên Grafana. Thử lại tin lỗi trước đó nếu cần; không coi dòng tin đang chờ trên UI là đã lưu thành công.

Khi kết thúc demo circuit:

```powershell
$demoHttp.Dispose()
$demoLogin = $null
$demoLoginBody = $null
$demoCredential = $null
```

Lời nói: “Khi Group không hoạt động, Chat không thể xác nhận quyền nên tạm từ chối thao tác. Polly giới hạn thời gian, retry có giới hạn và mở circuit để tránh gọi dồn. Seq lưu log tập trung của các instance; Grafana/Prometheus cho thấy lỗi và trạng thái. Khi Group hồi phục, request thử thành công đóng circuit lại. Polly không tự bật tiến trình Group.”

## 5. Auto-scaling: tạo tải để HPA tăng và giảm Pod

Xác nhận Group đã bật và hai Chat Pod Ready. Trong terminal theo dõi HPA (khai báo lại `$kc` nếu là cửa sổ mới):

```powershell
kubectl --kubeconfig $kc -n chatapp get hpa -w
```

Terminal khác theo dõi `get pods -w`. Terminal tạo tải:

```powershell
cd D:\Projects\ChatApp
node .\scripts\hpa-load.cjs --seconds=90 --workers=32
```

Script tạo tài khoản/nhóm fixture riêng, đọc history có xác thực, báo reads/errors/current/desired/CPU. Khi kết thúc nó tự dọn fixture và kiểm fingerprint dữ liệu giữ lại; đợi cả dòng cleanup. Không chạy suite khác tạo fixture đồng thời. Ctrl+C yêu cầu dừng tải và dọn, nên không đóng cưỡng bức terminal ngay khi cleanup chưa xong.

Quan sát CPU vượt mục tiêu đủ để HPA đề xuất thêm Pod; replicas có thể tăng 2 → 3 hoặc 4, Pod mới phải Ready. Xem Grafana `Pod CPU cores` và `Desired / current Chat replicas`. Không bắt buộc phải lên đúng 4; tùy tải và tài nguyên. Nếu tải 90s chưa tạo đủ CPU và hệ thống còn đáp ứng tốt, thử một lượt riêng `--seconds=180 --workers=64` sau khi lượt trước dọn xong. Không dùng `kubectl scale` thủ công để trình bày là HPA tự tăng.

Sau khi tải dừng, theo dõi CPU hạ và replicas dần trở về 2 nếu tải nền thấp. Scale-down có cửa sổ ổn định 90s và giới hạn một Pod/30s nên phải chờ thêm các chu kỳ đo/điều khiển, có thể vài phút. HPA giữ 2 không có nghĩa lỗi nếu vẫn còn tải.

Lời nói: “HPA dùng CPU của Pod để điều chỉnh số bản Chat từ 2 tới 4. Request mỗi Pod là 200m; mục tiêu 60% tương ứng khoảng 120m CPU trung bình, không phải 60% CPU cả máy. Tăng Pod giúp chia tải nhưng không tạo thêm CPU vật lý. WebSocket cũ vẫn ở Pod ban đầu; Pod mới nhận kết nối mới.”

## 6. Kết thúc và giới hạn cần nói rõ

```powershell
kubectl --kubeconfig $kc -n chatapp get pods
kubectl --kubeconfig $kc -n chatapp get hpa
Invoke-RestMethod 'http://localhost:5002/health/ready'
Invoke-RestMethod 'http://localhost:5080/health/ready'
```

Group Healthy, không còn maintenance, Pod Ready, web gửi/nhận lại được. HPA về 2 sau khi ổn định nếu không còn tải. Các bài này chứng minh chịu lỗi/scale ở tầng ứng dụng; PostgreSQL/Mongo/Kafka/Redis và kind vẫn chạy trên cùng một máy. Không tuyên bố đã chịu được mất cả máy, tự failover PostgreSQL hoặc giữ nguyên cuộc gọi video khi process signaling mất.

Nếu muốn chạy bộ kiểm thử tự động bao gồm circuit/readiness/reconnect/backplane/HPA thay cho thao tác thủ công:

```powershell
node .\scripts\resilience-test.cjs
```

Đây là một lượt kiểm thử riêng: script chủ động dừng/bật Group, thay Pod và tạo tải, có finally dọn fixture. Không chạy khi đang demo thủ công hoặc backup. Report ở `infra/backups/runtime/chapter8/resilience-test-report.json`; chỉ coi là qua khi lệnh kết thúc thành công và cleanup được xác nhận. Không cần chạy toàn bộ `test-resilience.ps1` chỉ để trình bày năm tiêu chí vì suite lớn còn chạy backup/restore và hồi quy các chương khác.
