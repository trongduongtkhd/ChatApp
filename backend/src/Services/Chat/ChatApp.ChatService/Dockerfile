# Image của chat-service. Build context = thư mục backend/ (cần BuildingBlocks/ và Directory.Build.props):
#   docker compose build chat-service-1
#
# Multi-stage: stage "build" dùng image SDK (có compiler, rất nặng) để biên dịch;
# stage cuối chỉ chép kết quả sang image aspnet (chỉ có runtime) → image chạy nhỏ hơn nhiều, không chứa mã nguồn.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Chép riêng các file .csproj rồi restore trước. Docker lưu cache theo từng lớp:
#    chỉ sửa code (.cs) thì lớp restore không đổi → lần build sau bỏ qua bước tải package.
COPY Directory.Build.props ./
COPY src/BuildingBlocks/ChatApp.Contracts/ChatApp.Contracts.csproj src/BuildingBlocks/ChatApp.Contracts/
COPY src/BuildingBlocks/ChatApp.Common/ChatApp.Common.csproj src/BuildingBlocks/ChatApp.Common/
COPY src/Services/Chat/ChatApp.ChatService/ChatApp.ChatService.csproj src/Services/Chat/ChatApp.ChatService/
RUN dotnet restore src/Services/Chat/ChatApp.ChatService/ChatApp.ChatService.csproj

# 2) Chép mã nguồn rồi publish. Grpc.Tools sinh code từ .proto ngay trong bước này (bản protoc cho Linux).
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Chat/ChatApp.ChatService/ src/Services/Chat/ChatApp.ChatService/
RUN dotnet publish src/Services/Chat/ChatApp.ChatService/ChatApp.ChatService.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# Npgsql thử nạp thư viện Kerberos (GSSAPI) khi mở kết nối; image aspnet không có sẵn → log báo
# "Cannot load library libgssapi_krb5.so.2" (vô hại nhưng gây nhiễu) → cài thêm.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# Chạy bằng user thường "app" (image có sẵn, mã số trong biến APP_UID), không chạy bằng root:
# lỡ bị chiếm quyền tiến trình thì kẻ tấn công cũng không có quyền root trong container.
USER $APP_UID
# Image aspnet mặc định nghe http://+:8080 (ASPNETCORE_HTTP_PORTS=8080).
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChatApp.ChatService.dll"]
