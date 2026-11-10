"""Create the one-page presentation version; reuse only the existing renderer definitions."""
from pathlib import Path

folder = Path(__file__).parent
renderer = (folder / 'generate_diagram.py').read_text(encoding='utf-8').split('\np=Page(')[0]
# This executes the renderer definitions, without regenerating the previous diagrams.
renderer = renderer.replace("f'chatapp-{self.number:02d}.svg'", "'ChatApp-Co-Logo.svg'")
renderer = renderer.replace("grid='1'", "grid='0'")
renderer = renderer.replace('arcSize=12;', 'arcSize=12;absoluteArcSize=1;')
exec(compile(renderer, str(folder / 'generate_diagram.py'), 'exec'), globals())

p = Page('Tổng thể hệ thống', 1, 'ChatApp · Microservices & Event-driven')
p.nodes[0]['title'] = 'TỔNG THỂ HỆ THỐNG & HẠ TẦNG TRIỂN KHAI'
p.nodes[0]['ts'] = 40
p.labels[0]['size'] = 23

# Application boundary and service layer.
p.box('backend',710,190,1480,890,'BACKEND · .NET 10',color='gray',group=True,title_size=22)
p.box('user',40,590,100,95,'USER',color='gray',title_size=23)
p.box('angular',180,565,205,140,'ANGULAR 12',['Giao diện web'],title_size=26,body_size=22)
p.box('gateway',465,565,205,140,'API GATEWAY',['YARP · JWT'],title_size=24,body_size=22)
p.box('identity',780,260,250,105,'IDENTITY SERVICE',['Tài khoản · Bạn bè'],title_size=23,body_size=21)
p.box('group',1120,260,250,105,'GROUP SERVICE',['Nhóm · Thành viên'],title_size=23,body_size=21)
p.box('presence',1460,260,250,105,'PRESENCE SERVICE',['Trạng thái online'],title_size=22,body_size=21,color='purple')
p.box('notification',1800,260,310,105,'NOTIFICATION SERVICE',['Số tin chưa đọc'],title_size=23,body_size=21)

# Database names remain visible; implementation details move out of the boxes.
p.box('identity-db',780,405,250,95,'POSTGRESQL',['identity_db · Outbox'],color='green',shape='cylinder',title_size=24,body_size=21)
p.box('group-db',1120,405,250,95,'POSTGRESQL',['group_db · Outbox'],color='green',shape='cylinder',title_size=24,body_size=21)
p.box('notification-db',1800,405,310,95,'POSTGRESQL',['notification_db'],color='green',shape='cylinder',title_size=24,body_size=21)

p.box('nginx',780,580,250,110,'NGINX',['Proxy / cân bằng tải'],title_size=27,body_size=21)
p.box('chat',1120,580,590,125,'CHAT SERVICE',['SignalR · Nhiều bản chạy'],title_size=29,body_size=24)
p.box('mongo',1800,580,310,125,'MONGODB',['Tin nhắn · 2 shard','Outbox'],color='green',title_size=27,body_size=22)
p.box('redis',1460,790,250,110,'REDIS',['Backplane · Presence'],color='teal',title_size=27,body_size=21)
p.box('storage',1800,790,310,110,'SEAWEEDFS',['Ảnh / File · S3 API'],color='green',title_size=27,body_size=22)
p.box('kafka',780,965,1330,85,'APACHE KAFKA',['Sự kiện bất đồng bộ'],color='orange',title_size=27,body_size=22)
p.box('peer',180,875,205,85,'TRÌNH DUYỆT B',color='teal',title_size=22)

# Main user path.
p.edge('user','angular',[(140,637),(180,637)])
p.edge('angular','gateway',[(385,637),(465,637)],both=True)
p.edge('gateway','nginx',[(670,637),(780,637)],both=True)
p.edge('nginx','chat',[(1030,637),(1120,637)],both=True)

# Shared Gateway routing trunk keeps the top row readable.
for target, x in [('identity',905),('group',1245),('notification',1955)]:
    p.edge('gateway',target,[(567,565),(567,240),(x,240),(x,260)])

for service,db,x in [('identity','identity-db',905),('group','group-db',1245),('notification','notification-db',1955)]:
    p.edge(service,db,[(x,365),(x,405)],'teal',True)

# Core RPC dependencies. Supporting account RPCs are omitted at overview scale.
p.edge('chat','identity',[(1190,580),(1190,540),(1065,540),(1065,312),(1030,312)],'purple')
p.edge('chat','group',[(1350,580),(1350,540),(1400,540),(1400,312),(1370,312)],'purple')
p.edge('chat','presence',[(1590,580),(1590,540),(1745,540),(1745,312),(1710,312)],'purple')
for x in [1105,1357,1625]:
    p.label(x,526,85,28,'gRPC',21,'purple',background='#F5F0FC')

# Shared data infrastructure.
p.edge('presence','redis',[(1585,365),(1585,410),(1730,410),(1730,845),(1710,845)],'teal',True)
p.edge('chat','mongo',[(1710,637),(1800,637)],'teal',True)
p.edge('chat','redis',[(1515,705),(1515,790)],'teal',True)
p.edge('chat','storage',[(1660,705),(1660,750),(1955,750),(1955,790)],'teal',True)

# Kafka bus: Identity publishes, Group and Chat publish/consume, Notification consumes.
p.edge('identity','kafka',[(780,312),(750,312),(750,1008),(780,1008)],'orange',dashed=True)
p.edge('group','kafka',[(1120,312),(1090,312),(1090,930),(1150,930),(1150,965)],'orange',True,True)
p.edge('chat','kafka',[(1220,705),(1220,965)],'orange',True,True)
p.edge('kafka','notification',[(2110,1008),(2150,1008),(2150,312),(2110,312)],'orange',dashed=True)
p.label(1250,917,330,32,'Outbox Publisher',22,'orange','left')

# WebRTC media is a client-to-client path, outside backend processing.
p.edge('angular','peer',[(282,705),(282,875)],'teal',True,True)
p.label(315,775,270,35,'WebRTC media',22,'teal','left')

# Only the most important infrastructure annotations stay on the presentation page.
p.box('deployment',50,1120,660,100,'TRIỂN KHAI',['Docker Compose · Kubernetes HPA'],color='blue',title_size=25,body_size=23)
p.box('monitoring',790,1120,660,100,'GIÁM SÁT & CHỊU LỖI',['Prometheus / Grafana · Polly'],color='gray',title_size=25,body_size=23)
p.box('backup',1530,1120,660,100,'SAO LƯU & PHỤC HỒI',['Hangfire · PITR'],color='green',title_size=25,body_size=23)

# Small, self-contained brand images; footer tools are grouped with their cards.
by_id={n['id']:n for n in p.nodes}
for source,output,viewbox in [
    ('angular.svg','angular-mark.svg','28 26 194 208'),
    ('nginx.svg','nginx-mark.svg','0 48 128 31'),
    ('docker.svg','docker-mark.svg','2 26 126 76'),
]:
    mark=ET.parse(folder/'assets'/source).getroot()
    mark.set('viewBox',viewbox)
    ET.ElementTree(mark).write(folder/'assets'/output,encoding='utf-8')
by_id['angular']['caption_offset']=25
p.image('logo-angular',252,576,61,52,folder/'assets/angular-mark.svg','angular')
# The Nginx wordmark supplies the heading; keep the one-line role underneath.
by_id['nginx']['title']=''
p.image('logo-nginx',827,587,156,48,folder/'assets/nginx-mark.svg','nginx')

for id in ['deployment','monitoring','backup']:
    by_id[id].update(y=1100,h=140,lines=[],top_title=True)

# Extract only the official Hangfire mark from its full horizontal wordmark.
hangfire=ET.parse(folder/'assets/hangfire.svg').getroot()
for child in list(hangfire)[1:]: hangfire.remove(child)
hangfire.set('viewBox','68.5 68.5 705.55 705.55')
ET.ElementTree(hangfire).write(folder/'assets/hangfire-mark.svg',encoding='utf-8')

for id,parent,asset,x,w,caption,caption_x,caption_w in [
    ('docker','deployment','docker-mark.svg',184,112,'Docker Compose',125,230),
    ('kubernetes','deployment','kubernetes.svg',501,58,'Kubernetes HPA',410,240),
    ('prometheus','monitoring','prometheus.svg',871,58,'Prometheus',805,190),
    ('grafana','monitoring','grafana.svg',1091,58,'Grafana',1025,190),
    ('polly','monitoring','polly.png',1311,58,'Polly',1245,190),
    ('hangfire','backup','hangfire-mark.svg',1613,54,'Hangfire',1545,190),
    ('pitr','backup','postgresql.svg',1833,54,'PostgreSQL · PITR',1750,220),
    ('mongo-backup','backup','mongodb.svg',2053,54,'MongoDB · Snapshot',1970,215),
]:
    p.image('logo-'+id,x,1148,w,54,folder/'assets'/asset,parent)
    p.label(caption_x,1208,caption_w,25,caption,20,'ink')
    p.labels[-1]['parent']=parent

# Small legend replaces repeated protocol labels beside every connector.
for x,color,label,dashed in [(65,'blue','REST / SignalR',False),(440,'purple','gRPC',False),
                             (740,'orange','Sự kiện Kafka',True),(1120,'teal','Dữ liệu / Pub/Sub',False)]:
    p.edges.append(dict(source=None,target=None,points=[(x,157),(x+60,157)],color=color,both=False,dashed=dashed))
    p.label(x+75,139,320,36,label,22,color,'left')
p.label(1700,139,480,36,'Triển khai local',21,'muted','right')

root=ET.Element('mxfile',host='app.diagrams.net',type='device',version='24.7.17',pages='1',compressed='false')
p.write(root)
ET.indent(root,space='  ')
for ext in ['io','drawio']:
    ET.ElementTree(root).write(folder / ('ChatApp-Kien-truc-Toi-gian.'+ext),encoding='utf-8',xml_declaration=True)
    ET.ElementTree(root).write(folder / ('ChatApp-Kien-truc-Co-Logo.'+ext),encoding='utf-8',xml_declaration=True)

# Check native editable cell references and title/body widths before rendering.
from PIL import ImageFont
cells=root.findall('.//mxCell')
ids={c.get('id') for c in cells}
assert len(ids)==len(cells)
for c in cells:
    for attr in ['source','target','parent']:
        assert c.get(attr) is None or c.get(attr) in ids
for n in p.nodes:
    assert len(n['lines'])<=2
    for value,size,bold in [(n['title'],n['ts'],True)]+[(v,n['bs'],False) for v in n['lines']]:
        font=ImageFont.truetype('C:/Windows/Fonts/arial'+('bd' if bold else '')+'.ttf',size)
        assert font.getlength(value)<=n['w']-20,(n['id'],value)
print('Validated: one page, at most 2 body lines per box, editable XML, no text overflow.')
assert len(p.images)==10
assert all(n['uri'].startswith('data:image/svg+xml,') for n in p.images)
print('Validated: 10 embedded logos; no external image dependencies.')
print(folder / 'ChatApp-Kien-truc-Co-Logo.io')
