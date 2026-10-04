# Demo gRPC chat-service → group-service (Phần 6, Chương 4).
# Từ Phần 7: endpoint tạm /debug/membership đã xóa; ChatHub gọi gRPC CheckMembership mỗi lần JoinGroup/SendMessage.
# Script này chạy chế độ "grpc" của scripts/chat-test.cs (client SignalR viết bằng C#):
#   Owner JoinGroup được; người chưa được thêm bị từ chối; vừa thêm → JoinGroup được NGAY; xóa nhóm → bị từ chối.
#
# Cần chạy trước: docker compose up -d, identity-service, group-service, Gateway, chat-service.
# Chạy: .\scripts\test-grpc.ps1 [-UserName duong] [-Password 123456] [-OtherUserName lan]
# Demo lỗi (group-service chết giữa chừng): dotnet run scripts/chat-test.cs -- down

param(
    [string]$UserName = "duong",
    [string]$Password = "123456",
    [string]$OtherUserName = "lan",
    [string]$GatewayUrl = "http://localhost:5000"
)

[Console]::OutputEncoding = [Text.Encoding]::UTF8
dotnet run "$PSScriptRoot/chat-test.cs" -- grpc --user $UserName --other $OtherUserName --password $Password --gateway $GatewayUrl
exit $LASTEXITCODE
