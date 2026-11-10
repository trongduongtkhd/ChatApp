"""Generate editable diagrams.net XML and matching SVG previews. No application changes."""
from pathlib import Path
import html
import base64
from urllib.parse import quote
import xml.etree.ElementTree as ET

OUT = Path(__file__).parent
W, H = 2240, 1260
C = dict(ink='#19324D', muted='#5B6E82', blue='#2563EB', purple='#8754C5',
         orange='#D77A18', teal='#078B83', green='#32835B', gray='#7890A5')
PALE = dict(blue='#EEF5FF', purple='#F5F0FC', orange='#FFF6E9', teal='#EBF9F6',
            green='#F0F8F1', gray='#F6F8FB', ink='#EDF2F7')

class Page:
    def __init__(self, name, number, subtitle):
        self.name, self.number = name, number
        self.nodes, self.edges, self.labels, self.images = [], [], [], []
        self.box('title', 50, 30, 2140, 65, 'KIẾN TRÚC TỔNG THỂ HỆ THỐNG & HẠ TẦNG TRIỂN KHAI',
                 [], 'ink', title_size=39, text=True, align='left')
        self.label(50, 102, 2140, 32, subtitle, 22, 'muted', 'left')

    def box(self, id, x,y,w,h,title,lines=(),color='blue',shape='rect',
            title_size=24,body_size=20,group=False,text=False,align='center',dashed=False):
        self.nodes.append(dict(id=id,x=x,y=y,w=w,h=h,title=title,lines=list(lines),color=color,
                               shape=shape,ts=title_size,bs=body_size,group=group,text=text,align=align,dashed=dashed))
        return id

    def label(self,x,y,w,h,value,size=18,color='muted',align='center',background=None):
        self.labels.append(dict(x=x,y=y,w=w,h=h,value=value,size=size,color=color,align=align,background=background))

    def edge(self,source,target,points,color='blue',both=False,dashed=False):
        self.edges.append(dict(source=source,target=target,points=points,color=color,both=both,dashed=dashed))

    def image(self,id,x,y,w,h,path,parent=None):
        path=Path(path)
        raw=path.read_bytes()
        if path.suffix.lower()=='.svg':
            svg=ET.tostring(ET.fromstring(raw),encoding='unicode')
            preview='data:image/svg+xml;base64,'+base64.b64encode(svg.encode()).decode()
        else:
            preview='data:image/png;base64,'+base64.b64encode(raw).decode()
            svg=f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}"><image href="{preview}" width="{w}" height="{h}" preserveAspectRatio="xMidYMid meet"/></svg>'
        # Percent-encoded SVG avoids semicolons inside the diagrams.net style value.
        self.images.append(dict(id=id,x=x,y=y,w=w,h=h,parent=parent,
            uri='data:image/svg+xml,'+quote(svg,safe=''),preview=preview))

    def legend(self):
        for x,color,label,dash in [(65,'blue','REST / SignalR',False),(395,'purple','gRPC nội bộ',False),
                                   (715,'orange','Kafka / sự kiện',True),(1085,'teal','Dữ liệu / Pub/Sub / media',False)]:
            self.edges.append(dict(source=None,target=None,points=[(x,160),(x+58,160)],color=color,both=False,dashed=dash))
            self.label(x+70,146,310,28,label,18,color,'left')
        self.label(1740,146,440,28,f'{self.number:02d} / 02  ·  {self.name}',18,'muted','right')

    def write(self, root):
        diagram=ET.SubElement(root,'diagram',id='chatapp-'+str(self.number),name=self.name)
        model=ET.SubElement(diagram,'mxGraphModel',dx=str(W),dy=str(H),grid='1',gridSize='10',guides='1',
            tooltips='1',connect='1',arrows='1',fold='1',page='1',pageScale='1',pageWidth=str(W),pageHeight=str(H),math='0',shadow='0',background='#FFFFFF')
        cells=ET.SubElement(model,'root')
        ET.SubElement(cells,'mxCell',id='0')
        ET.SubElement(cells,'mxCell',id='1',parent='0')
        svg=[f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">',
             '<defs>']
        for k,v in C.items():
            svg.append(f'<marker id="arrow-{k}" markerWidth="10" markerHeight="10" refX="9" refY="5" orient="auto-start-reverse" markerUnits="userSpaceOnUse"><path d="M1 1 L9 5 L1 9 z" fill="{v}"/></marker>')
        svg.extend(['</defs>','<rect width="2240" height="1260" fill="white"/>'])
        # Backgrounds, then connectors, then cards, then labels; identical stacking in XML/SVG.
        lookup={n['id']:n for n in self.nodes}
        def add_node(n):
            x,y,w,h=n['x'],n['y'],n['w'],n['h']
            color=C[n['color']]; fill=PALE[n['color']]
            title=html.escape(n['title'])
            body=''.join('<br/>'+html.escape(line) for line in n['lines'])
            value=f'<b><font style="font-size:{n["ts"]}px">{title}</font></b><font style="font-size:{n["bs"]}px">{body}</font>'
            style=f'rounded=1;arcSize=12;whiteSpace=wrap;html=1;fontFamily=Arial;fontColor={C["ink"]};fontSize={n["bs"]};strokeColor={color};fillColor={fill};strokeWidth=1.7;spacing=10;align={n["align"]};verticalAlign=middle;'
            if n['shape']=='cylinder': style+='shape=cylinder3;size=15;boundedLbl=1;backgroundOutline=1;verticalAlign=bottom;spacingBottom=10;'
            if n['group']: style+='verticalAlign=top;spacingTop=15;align=left;spacingLeft=20;'
            if n['text']: style+='fillColor=none;strokeColor=none;spacing=0;'
            if n['dashed']: style+='dashed=1;dashPattern=7 5;'
            if n.get('caption_offset'): style+=f'spacingTop={10+2*n["caption_offset"]};spacingBottom=10;'
            if n.get('top_title'): style+='verticalAlign=top;spacingTop=15;'
            cell=ET.SubElement(cells,'mxCell',id=n['id'],value=value,style=style,vertex='1',parent='1')
            ET.SubElement(cell,'mxGeometry',x=str(x),y=str(y),width=str(w),height=str(h),attrib={'as':'geometry'})
            dash=' stroke-dasharray="7 5"' if n['dashed'] else ''
            if not n['text']:
                if n['shape']=='cylinder':
                    svg.append(f'<path d="M{x} {y+15} C{x} {y-5},{x+w} {y-5},{x+w} {y+15} L{x+w} {y+h-15} C{x+w} {y+h+5},{x} {y+h+5},{x} {y+h-15} Z" fill="{fill}" stroke="{color}" stroke-width="1.7"{dash}/><path d="M{x} {y+15} C{x} {y+35},{x+w} {y+35},{x+w} {y+15}" fill="none" stroke="{color}" stroke-width="1.3"/>')
                else:
                    svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" fill="{fill}" stroke="{color}" stroke-width="1.7"{dash}/>')
            align=n['align'] if not n['group'] else 'left'
            tx=x+20 if align=='left' else x+w-12 if align=='right' else x+w/2
            anchor='start' if align=='left' else 'end' if align=='right' else 'middle'
            total=n['ts']+len(n['lines'])*(n['bs']+9)
            ty=y+32 if n['group'] else y+(h-total)/2+n['ts']*.82
            if n['shape']=='cylinder': ty+=10
            ty+=n.get('caption_offset',0)
            if n.get('top_title'): ty=y+34
            svg.append(f'<text x="{tx}" y="{ty}" font-family="Arial, sans-serif" font-size="{n["ts"]}" font-weight="700" text-anchor="{anchor}" fill="{C["ink"]}">{title}</text>')
            for i,line in enumerate(n['lines']):
                sy=ty+n['bs']+11+i*(n['bs']+9)
                svg.append(f'<text x="{tx}" y="{sy}" font-family="Arial, sans-serif" font-size="{n["bs"]}" text-anchor="{anchor}" fill="{C["ink"]}">{html.escape(line)}</text>')

        for n in self.nodes:
            if n['group']: add_node(n)
        for i,e in enumerate(self.edges):
            color=C[e['color']]; pts=e['points']
            style=f'edgeStyle=orthogonalEdgeStyle;rounded=0;html=1;strokeColor={color};strokeWidth=2.3;endArrow=block;endFill=1;jettySize=0;orthogonalLoop=1;'
            if e['dashed']: style+='dashed=1;dashPattern=7 5;'
            if e['both']: style+='startArrow=block;startFill=1;'
            attrs=dict(id='edge-'+str(i),style=style,edge='1',parent='1')
            for prop,p,word in [('source',pts[0],'exit'),('target',pts[-1],'entry')]:
                if e[prop]:
                    attrs[prop]=e[prop]; n=lookup[e[prop]]
                    attrs['style']+=f'{word}X={(p[0]-n["x"])/n["w"]:.6f};{word}Y={(p[1]-n["y"])/n["h"]:.6f};{word}Dx=0;{word}Dy=0;{word}Perimeter=0;'
            cell=ET.SubElement(cells,'mxCell',**attrs)
            g=ET.SubElement(cell,'mxGeometry',relative='1',attrib={'as':'geometry'})
            if not e['source']: ET.SubElement(g,'mxPoint',x=str(pts[0][0]),y=str(pts[0][1]),attrib={'as':'sourcePoint'})
            if not e['target']: ET.SubElement(g,'mxPoint',x=str(pts[-1][0]),y=str(pts[-1][1]),attrib={'as':'targetPoint'})
            if len(pts)>2:
                a=ET.SubElement(g,'Array',attrib={'as':'points'})
                for x,y in pts[1:-1]: ET.SubElement(a,'mxPoint',x=str(x),y=str(y))
            d='M'+' L'.join(f'{x},{y}' for x,y in pts)
            dash=' stroke-dasharray="7 5"' if e['dashed'] else ''
            start=f' marker-start="url(#arrow-{e["color"]})"' if e['both'] else ''
            svg.append(f'<path d="{d}" fill="none" stroke="{color}" stroke-width="2.3" marker-end="url(#arrow-{e["color"]})"{start}{dash}/>')
        for n in self.nodes:
            if not n['group']: add_node(n)
        for n in self.images:
            x,y,w,h=n['x'],n['y'],n['w'],n['h']
            parent=n['parent'] or '1'
            style='shape=image;imageAspect=1;aspect=fixed;verticalLabelPosition=bottom;verticalAlign=top;image='+n['uri']+';'
            cell=ET.SubElement(cells,'mxCell',id=n['id'],value='',style=style,vertex='1',parent=parent,connectable='0')
            px,py=(lookup[parent]['x'],lookup[parent]['y']) if parent!='1' else (0,0)
            ET.SubElement(cell,'mxGeometry',x=str(x-px),y=str(y-py),width=str(w),height=str(h),attrib={'as':'geometry'})
            svg.append(f'<image x="{x}" y="{y}" width="{w}" height="{h}" href="{n["preview"]}" preserveAspectRatio="xMidYMid meet"/>')
        for i,n in enumerate(self.labels):
            x,y,w,h=n['x'],n['y'],n['w'],n['h']
            style=f'text;html=1;whiteSpace=wrap;strokeColor=none;fillColor={n.get("background") or "none"};fontFamily=Arial;fontSize={n["size"]};fontColor={C[n["color"]]};align={n["align"]};verticalAlign=middle;spacing=0;'
            parent=n.get('parent') or '1'
            cell=ET.SubElement(cells,'mxCell',id='label-'+str(i),value=html.escape(n['value']),style=style,vertex='1',parent=parent)
            px,py=(lookup[parent]['x'],lookup[parent]['y']) if parent!='1' else (0,0)
            ET.SubElement(cell,'mxGeometry',x=str(x-px),y=str(y-py),width=str(w),height=str(h),attrib={'as':'geometry'})
            if n.get('background'):
                svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="4" fill="{n["background"]}"/>')
            tx=x if n['align']=='left' else x+w if n['align']=='right' else x+w/2
            anchor='start' if n['align']=='left' else 'end' if n['align']=='right' else 'middle'
            svg.append(f'<text x="{tx}" y="{y+h/2+n["size"]*.34}" font-family="Arial, sans-serif" font-size="{n["size"]}" text-anchor="{anchor}" fill="{C[n["color"]]}">{html.escape(n["value"])}</text>')
        svg.append('</svg>')
        (OUT/f'chatapp-{self.number:02d}.svg').write_text('\n'.join(svg),encoding='utf-8')

p=Page('Tổng thể hệ thống',1,'ChatApp · Microservices + Event-driven · Thành phần và luồng giao tiếp chính')
p.box('system',720,190,1470,890,'BACKEND .NET 10  ·  DỊCH VỤ ĐỘC LẬP',color='gray',group=True,title_size=20)
p.box('user',40,565,100,115,'USER',['Người dùng'],title_size=20,body_size=15,color='gray')
p.box('web',170,525,220,180,'ANGULAR 12',['Chat / Danh bạ','Quản trị / Sao lưu','RxJS · WebRTC'],body_size=20)
p.box('gateway',455,540,210,150,'API GATEWAY',['YARP · JWT', 'REST / SignalR', 'Hangfire backup'],title_size=23)
p.box('identity',780,250,250,110,'IDENTITY SERVICE',['Tài khoản · Bạn bè','JWT · Khóa / Vai trò'],body_size=19)
p.box('group',1120,250,250,110,'GROUP SERVICE',['Nhóm · Thành viên','Role / Phê duyệt'],body_size=19)
p.box('presence',1460,250,250,110,'PRESENCE SERVICE',['Heartbeat / lease','gRPC · Redis TIME'],color='purple',title_size=22,body_size=19)
p.box('notification',1800,250,300,110,'NOTIFICATION SERVICE',['Bộ đếm chưa đọc','SignalR notifications'],title_size=23,body_size=19)
p.box('id-db',780,390,250,95,'identity_db',['PostgreSQL · SQL Outbox'],color='green',shape='cylinder',title_size=22,body_size=18)
p.box('group-db',1120,390,250,95,'group_db',['PostgreSQL · SQL Outbox'],color='green',shape='cylinder',title_size=22,body_size=18)
p.box('notification-db',1800,390,300,95,'notification_db',['Unread · processed_events'],color='green',shape='cylinder',title_size=22,body_size=18)
p.box('nginx',780,580,250,110,'NGINX',['Proxy / Load balancing','HTTP + WebSocket'],body_size=19)
p.box('chat',1120,580,590,135,'CHAT SERVICE  ·  NHIỀU BẢN CHẠY',['SignalR chat / typing / video signaling','REST lịch sử / ảnh / file · Mongo Outbox','Kubernetes 2–4 Pod hoặc Compose 2 container'],body_size=19)
p.box('mongo',1800,580,300,135,'MONGODB',['chat_history · 2 shards','Message + Counter + Outbox','Transaction / Idempotency'],color='green',body_size=18)
p.box('redis',1460,795,250,110,'REDIS',['SignalR Backplane','Presence / call / lease'],color='teal',body_size=18)
p.box('objects',1800,795,300,110,'SEAWEEDFS',['Kho ảnh / file local','S3 API nội bộ · Volume'],color='green',body_size=19)
p.box('kafka',780,960,1320,95,'APACHE KAFKA  ·  KRaft  ·  XỬ LÝ BẤT ĐỒNG BỘ',['Identity → Group  |  Group → Chat + Notification  |  Chat → Notification','Transactional Outbox → at-least-once → consumer chống trùng / cập nhật bản sao'],color='orange',title_size=24,body_size=19)
p.box('peer',170,855,220,95,'TRÌNH DUYỆT B',['WebRTC audio / video'],color='teal',title_size=22,body_size=18)
p.box('rpc-note',445,780,250,170,'RPC NGUỒN QUYỀN',['Gateway (backup),','Group / Notification','→ Identity gRPC','JWT + AuthVersion'],color='purple',title_size=18,body_size=17)
p.edge('user','web',[(140,620),(170,620)])
p.edge('web','gateway',[(390,620),(455,620)],both=True)
p.label(400,482,310,30,'HTTP / WebSocket + JWT',19,'blue')
for target,end,lane in [('identity',905,235),('group',1245,235),('notification',1950,235)]:
    p.edge('gateway',target,[(570,540),(570,lane),(end,lane),(end,250)])
p.edge('gateway','nginx',[(665,635),(780,635)],both=True)
p.edge('nginx','chat',[(1030,635),(1120,635)],both=True)
for a,b,x in [('identity','id-db',905),('group','group-db',1245),('notification','notification-db',1950)]:
    p.edge(a,b,[(x,360),(x,390)],'teal',True)
p.label(780,489,590,28,'Ba database riêng trên một PostgreSQL instance',18,'green')
p.edge('chat','identity',[(1190,580),(1190,550),(1065,550),(1065,305),(1030,305)],'purple')
p.edge('chat','group',[(1350,580),(1350,535),(1395,535),(1395,305),(1370,305)],'purple')
p.edge('chat','presence',[(1590,580),(1590,535),(1745,535),(1745,305),(1710,305)],'purple')
p.label(1100,550,580,26,'gRPC: tài khoản / thành viên / trạng thái online',18,'purple')
p.edge('presence','redis',[(1585,360),(1585,390),(1730,390),(1730,850),(1710,850)],'teal',True)
p.edge('chat','mongo',[(1710,635),(1800,635)],'teal',True)
p.label(1717,600,76,28,'Driver',17,'teal')
p.edge('chat','redis',[(1515,715),(1515,795)],'teal',True)
p.label(1240,742,270,28,'Pub/Sub · Backplane',18,'teal')
p.edge('chat','objects',[(1660,715),(1660,755),(1950,755),(1950,795)],'teal',True)
p.label(1790,724,310,28,'S3 / binary qua REST',18,'teal')
p.edge('identity','kafka',[(780,305),(750,305),(750,1005),(780,1005)],'orange',False,True)
p.edge('group','kafka',[(1120,305),(1090,305),(1090,925),(1150,925),(1150,960)],'orange',True,True)
p.edge('chat','kafka',[(1220,715),(1220,960)],'orange',True,True)
p.label(1240,883,200,28,'Outbox / consumer',18,'orange','left')
p.edge('kafka','notification',[(2100,1005),(2140,1005),(2140,305),(2100,305)],'orange',False,True)
p.edge('web','peer',[(280,705),(280,855)],'teal',True,True)
p.label(80,753,400,28,'Media trực tiếp hoặc TURN relay',18,'teal')
p.label(70,795,330,25,'STUN / TURN tùy chọn',17,'muted')
p.label(70,825,330,25,'Signaling qua Chat',17,'muted')
for x,id,title,lines,col in [
    (50,'ops','GIÁM SÁT',['Serilog → Seq · Prometheus → Grafana','Health / metrics / logs theo service & Pod'],'gray'),
    (595,'fault','CHỊU LỖI',['Polly timeout / retry / circuit breaker','Probes · HPA · Reconnect + REST lấy bù'],'blue'),
    (1140,'sync','ĐỊNH DANH & ĐỒNG BỘ',['JWT / AuthVersion · GUID · Docker DNS','SQL xmin · Mongo transaction · lease / fencing'],'purple'),
    (1685,'backup','SAO LƯU & PHỤC HỒI',['Hangfire · PG base + WAL / PITR · Standby','Checkpoint Mongo / Kafka / object → Web riêng'],'green')]:
    p.box(id,x,1110,505,105,title,lines,col,title_size=21,body_size=17)
p.label(50,1220,2140,24,'Màu chỉ loại giao tiếp; mũi tên hai đầu thể hiện trao đổi hai chiều. Các nhánh là luồng logic, không biểu thị mỗi request đi qua mọi service.',17,'muted','left')
p.legend()

q=Page('Hạ tầng triển khai',2,'Triển khai local theo mã nguồn · Mode Chat được chọn: Kubernetes kind · Có chế độ Compose thay thế')
q.box('machine',50,190,2140,885,'MÁY WINDOWS  ·  DOCKER DESKTOP  ·  PHẠM VI LOCAL CỦA ĐỒ ÁN',color='gray',group=True,title_size=22)
q.box('local',80,250,460,740,'TIẾN TRÌNH TRÊN WINDOWS',color='blue',group=True,title_size=21)
q.box('docker',590,250,1560,740,'CONTAINER DOCKER  ·  COMPOSE + NODE kind  ·  MẠNG chatapp-net',color='gray',group=True,title_size=21)
q.box('kind',980,290,1120,270,'KUBERNETES kind  ·  1 NODE  ·  namespace: chatapp',color='blue',group=True,title_size=22)
q.box('front',110,310,400,80,'ANGULAR 12  ·  :4200',['Node 14 · ng serve / web server local'],title_size=22,body_size=18)
q.box('gw',110,440,400,95,'GATEWAY YARP  ·  :5000',['JWT · Định tuyến · Hangfire / backup_db'],title_size=23,body_size=18)
q.box('id',110,590,400,75,'IDENTITY  ·  :5001 / gRPC :5011',title_size=21)
q.box('grp',110,695,400,75,'GROUP  ·  :5002 / gRPC :5012',title_size=21)
q.box('not',110,800,400,75,'NOTIFICATION  ·  :5004',title_size=21)
q.box('agent',110,910,400,55,'Operations agent  ·  :18080',color='purple',title_size=20)
q.box('nx',630,320,280,110,'NGINX  ·  :5080',['Container :80','Proxy HTTP / WebSocket'],title_size=23,body_size=19)
q.box('svc',1015,360,230,115,'CHAT SERVICE',['NodePort :30080','Service → Pod :8080'],title_size=22,body_size=18)
q.box('pod1',1300,360,210,115,'CHAT POD 1',['.NET 10 · SignalR','Probe / Outbox'],title_size=22,body_size=18)
q.box('pod2',1540,360,210,115,'CHAT POD 2',['Cùng image','Redis Backplane'],title_size=22,body_size=18)
q.box('pod34',1780,360,280,115,'POD 3 / 4 KHI CẦN',['HPA tăng / giảm tải','CPU request 200m / Pod'],title_size=22,body_size=18,dashed=True)
q.label(1000,501,1080,36,'Metrics Server → HPA 2–4 Pod · CPU 60% requests · scale-down stabilization 90s',19,'purple')
q.box('pg',630,625,400,210,'POSTGRESQL 17  ·  :5432',['identity_db / group_db','notification_db / backup_db','chat_db: legacy, dùng đối chiếu','Primary + data volume + WAL archive'],color='green',title_size=24,body_size=19)
q.box('standby',630,870,400,85,'POSTGRESQL STANDBY',['Streaming bất đồng bộ · Volume riêng'],color='green',title_size=22,body_size=18)
q.box('mongog',1090,625,500,235,'MONGODB 8.0.34  ·  chat_history',color='green',group=True,title_size=23)
q.box('router',1110,685,460,55,'mongos  ·  mongo-router:27017',color='green',title_size=22)
q.box('cfg',1110,780,140,60,'CONFIG RS',['1 node'],color='green',title_size=19,body_size=17)
q.box('shard1',1270,780,140,60,'SHARD 1',['RS · 1 node'],color='green',title_size=19,body_size=17)
q.box('shard2',1430,780,140,60,'SHARD 2',['RS · 1 node'],color='green',title_size=19,body_size=17)
q.label(1080,877,530,30,'messages / outbox: shard key {groupId: hashed}',18,'green')
q.box('rds',1650,625,210,135,'REDIS 7.4',[':6379 · Backplane','Presence / call','AOF everysec'],color='teal',title_size=23,body_size=18)
q.box('s3',1900,625,210,135,'SEAWEEDFS',['S3 :8333 nội bộ','Ảnh / File','Volume dữ liệu'],color='green',title_size=22,body_size=18)
q.box('pres',1650,825,210,110,'PRESENCE',['gRPC :5015 nội bộ','Health :5005'],color='purple',title_size=22,body_size=18)
q.box('broker',1900,825,210,110,'KAFKA KRaft',[':9092 / nội bộ :29092','1 broker · UI :8080'],color='orange',title_size=22,body_size=17)
q.edge('front','gw',[(310,390),(310,440)],both=True)
q.edge('gw','nx',[(510,485),(560,485),(560,375),(630,375)],both=True)
q.edge('nx','svc',[(910,375),(960,375),(960,405),(1015,405)],both=True)
q.edge('svc','pod1',[(1245,400),(1300,400)],both=True)
q.edge('svc','pod2',[(1245,445),(1270,445),(1270,488),(1645,488),(1645,475)],both=True)
q.edge('svc','pod34',[(1245,460),(1260,460),(1260,491),(1920,491),(1920,475)],dashed=True)
q.edge('id','pg',[(510,625),(630,665)],'teal')
# Explicit orthogonal waypoints for the SQL connections.
q.edges[-1]['points']=[(510,625),(570,625),(570,665),(630,665)]
q.edge('grp','pg',[(510,732),(630,732)],'teal')
q.edge('not','pg',[(510,837),(570,837),(570,800),(630,800)],'teal')
q.label(555,578,200,28,'Npgsql / SQL',18,'teal')
q.edge('pg','standby',[(830,835),(830,870)],'teal')
q.edge('kind','router',[(1340,560),(1065,560),(1065,712),(1110,712)],'teal',True)
q.label(1150,575,380,28,'MongoDB Driver / transaction',18,'teal')
q.edge('router','cfg',[(1175,740),(1175,780)],'teal',True)
q.edge('router','shard1',[(1340,740),(1340,780)],'teal',True)
q.edge('router','shard2',[(1500,740),(1500,780)],'teal',True)
q.edge('kind','rds',[(1755,560),(1755,625)],'teal',True)
q.edge('kind','s3',[(2005,560),(2005,625)],'teal',True)
q.edge('pres','rds',[(1755,825),(1755,760)],'teal',True)
q.edge('kind','pres',[(2100,530),(2130,530),(2130,970),(1875,970),(1875,880),(1860,880)],'purple')
q.edge('kind','broker',[(2100,545),(2165,545),(2165,955),(2005,955),(2005,935)],'orange',True,True)
q.label(80,1015,2090,28,'RPC Pod → Identity / Group: host.docker.internal:5011 / :5012  ·  Container dùng Docker DNS; Pod dùng hostAliases theo script setup.',19,'purple','left')
q.label(80,1045,2090,24,'Mode thay thế: Nginx → 2 Chat container (:5003 / :5013). Chỉ một mode Chat phục vụ nguồn tại một thời điểm.',18,'muted','left')
q.box('monitor',50,1100,1030,110,'GIÁM SÁT  ·  COMPOSE RIÊNG',['Serilog → Seq :5341  |  /metrics → Prometheus :9090 → Grafana :3000','Operations agent → API K8s / từng Pod  |  Metrics Server cấp CPU cho HPA'],color='gray',title_size=23,body_size=19)
q.box('bkp',1120,1100,1070,110,'SAO LƯU  ·  BUNDLE LOCAL → PHỤC HỒI CÁCH LY',['Gateway / Hangfire → PG base + WAL, Mongo snapshot, Kafka offsets, file + SHA-256','Restore → chatapp-recovery · Web :4201 · Network / volumes riêng · Tắt lịch backup'],color='green',title_size=23,body_size=19)
q.label(50,1222,2140,25,'GIỚI HẠN: một máy / một node kind; Mongo replica set một node; Kafka / Redis / kho file chưa HA. Standby không tự failover. Backup local chưa bảo vệ mất toàn máy.',18,'muted','left')
q.legend()

root=ET.Element('mxfile',host='app.diagrams.net',type='device',version='24.7.17',pages='2',compressed='false')
for page in [p,q]: page.write(root)
ET.indent(root,space='  ')
path=OUT/'ChatApp-Tong-the-He-thong-va-Ha-tang.drawio'
ET.ElementTree(root).write(path,encoding='utf-8',xml_declaration=True)
# Structural checks validate references and page geometry without invoking the application.
doc=ET.parse(path)
for diagram in doc.findall('diagram'):
    cells=diagram.findall('.//mxCell'); ids={c.get('id') for c in cells}
    assert len(ids)==len(cells), 'Duplicate IDs'
    for c in cells:
        for attr in ['source','target','parent']:
            if c.get(attr): assert c.get(attr) in ids, (attr,c.get(attr))
    print(diagram.get('name'), ':',len(cells),'editable cells;',sum(c.get('edge')=='1' for c in cells),'connectors')
print(path)
