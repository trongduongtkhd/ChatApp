import { Injectable, NgZone } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { VideoCall, VideoCallConfig, VideoCallSignal, VideoCallView } from '../models/video-call';
import { uuidv7 } from '../utils/guid';
import { AuthService } from './auth.service';
import { ChatHubService } from './chat-hub.service';

@Injectable({ providedIn: 'root' })
export class VideoCallService {
  private readonly viewSubject = new BehaviorSubject<VideoCallView | null>(null);
  private readonly localSubject = new BehaviorSubject<MediaStream | null>(null);
  private readonly remoteSubject = new BehaviorSubject<MediaStream | null>(null);
  readonly view$ = this.viewSubject.asObservable();
  readonly localStream$ = this.localSubject.asObservable();
  readonly remoteStream$ = this.remoteSubject.asObservable();
  ready = false;
  microphoneEnabled = true;
  cameraEnabled = true;
  private config: VideoCallConfig | null = null;
  private setup: Promise<void> = Promise.resolve();
  private call: VideoCall | null = null;
  private peer: RTCPeerConnection | null = null;
  private generation = 0;
  private transportGeneration = 0;
  private offerStarted = false;
  private candidates: RTCIceCandidateInit[] = [];
  private signalQueue = Promise.resolve();
  private timer?: ReturnType<typeof setInterval>;
  private recoveryTimer?: ReturnType<typeof setTimeout>;
  private connectingTimer?: ReturnType<typeof setTimeout>;
  private pulsing = false;
  private readonly versions = new Map<string, number>();
  private readonly ended = new Set<string>();
  get view(): VideoCallView | null { return this.viewSubject.value; }
  get busy(): boolean { return !!this.view && this.view.phase !== 'ended'; }

  constructor(private readonly hub: ChatHubService, private readonly auth: AuthService, private readonly zone: NgZone) {
    hub.state$.subscribe(state => {
      const epoch = ++this.transportGeneration;
      this.ready = false; this.config = null;
      if (state === 'connected') {
        this.setup = this.configure(epoch).catch(() => undefined);
      } else if (this.busy) this.finish('Kết nối ứng dụng bị gián đoạn. Hãy gọi lại sau khi kết nối phục hồi.');
    });
    hub.callEvents$.subscribe(call => { void this.setup.then(() => this.apply(call)); });
    hub.callSignals$.subscribe(signal => {
      this.signalQueue = this.signalQueue.then(() => this.receiveSignal(signal)).catch(() => {
        if (this.call?.id === signal.callId) this.fail('Không thiết lập được cuộc gọi. Hãy kiểm tra mạng hoặc cấu hình STUN/TURN.');
      });
    });
    auth.currentUser$.subscribe(user => { if (!user) this.reset(); });
    window.addEventListener('pagehide', () => { this.stopMedia(); });
  }
  private async configure(epoch: number): Promise<void> {
    const config = await this.hub.callInvoke<VideoCallConfig>('GetVideoCallConfig');
    if (epoch !== this.transportGeneration) return;
    this.config = config; this.ready = true;
    const call = await this.hub.callInvoke<VideoCall | null>('GetCurrentVideoCall');
    if (epoch === this.transportGeneration && call) this.apply(call);
  }
  async start(peerId: string, peerName: string): Promise<void> {
    if (this.busy) return;
    const id = uuidv7(); const epoch = ++this.generation;
    this.viewSubject.next({ id, peerId, peerName, phase: 'preparing', message: 'Đang mở camera và microphone…', connectedAt: 0 });
    try {
      await this.setup;
      if (!this.ready) throw new Error('Dịch vụ gọi video chưa sẵn sàng. Hãy chờ kết nối hoặc tải lại trang.');
      this.config = await this.hub.callInvoke<VideoCallConfig>('GetVideoCallConfig');
      if (epoch !== this.generation) return;
      if (!await this.capture(epoch)) return;
      const call = await this.hub.callInvoke<VideoCall>('StartVideoCall', id, peerId);
      if (epoch !== this.generation) { void this.hub.callInvoke('EndVideoCall', id, 'cancelled').catch(() => undefined); return; }
      this.apply(call);
    } catch (error) { if (epoch === this.generation) this.finish(this.friendly(error)); }
  }
  async accept(): Promise<void> {
    const call = this.call;
    if (!call || this.view?.phase !== 'incoming') return;
    const epoch = ++this.generation;
    this.viewSubject.next({ ...this.view!, phase: 'preparing', message: 'Đang mở camera và microphone…' });
    try {
      this.config = await this.hub.callInvoke<VideoCallConfig>('GetVideoCallConfig');
      if (epoch !== this.generation) return;
      if (!await this.capture(epoch)) return;
      this.ensurePeer();
      const result = await this.hub.callInvoke<VideoCall>('AcceptVideoCall', call.id);
      if (epoch === this.generation) this.apply(result);
    } catch (error) { if (epoch === this.generation) this.fail(this.friendly(error)); }
  }
  async hangup(reason = 'hangup'): Promise<void> {
    const id = this.view?.id;
    const message = reason === 'declined' ? 'Bạn đã từ chối cuộc gọi.' : 'Cuộc gọi đã kết thúc.';
    this.finish(message);
    if (id) { try { await this.hub.callInvoke('EndVideoCall', id, reason); } catch { /* disconnect/lease phía server thu hồi */ } }
  }
  dismiss(): void { if (!this.busy) { this.viewSubject.next(null); this.call = null; } }
  toggleMicrophone(): void {
    this.microphoneEnabled = !this.microphoneEnabled;
    this.localSubject.value?.getAudioTracks().forEach(t => t.enabled = this.microphoneEnabled);
  }
  toggleCamera(): void {
    this.cameraEnabled = !this.cameraEnabled;
    this.localSubject.value?.getVideoTracks().forEach(t => t.enabled = this.cameraEnabled);
  }
  private async capture(epoch: number): Promise<boolean> {
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia)
      throw new Error('Camera cần HTTPS hoặc localhost. Hãy mở ChatApp bằng link HTTPS của Cloudflare Tunnel.');
    const stream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 640 }, height: { ideal: 360 }, frameRate: { ideal: 24, max: 30 } }, audio: true });
    if (epoch !== this.generation) { stream.getTracks().forEach(t => t.stop()); return false; }
    this.localSubject.value?.getTracks().forEach(t => t.stop());
    this.microphoneEnabled = true; this.cameraEnabled = true; this.localSubject.next(stream); return true;
  }
  private apply(call: VideoCall): void {
    const me = this.auth.currentUser?.id;
    if (!this.config || (call.callerId !== me && call.calleeId !== me) || this.ended.has(call.id)) return;
    if (call.version < (this.versions.get(call.id) || 0)) return;
    this.versions.set(call.id, call.version);
    if (this.versions.size > 128) this.versions.delete(this.versions.keys().next().value!);
    const caller = call.callerId === me;
    if (caller && call.callerConnectionId !== this.config.connectionId) return;
    if (!caller && call.state !== 'Ringing' && call.state !== 'Ended' && call.calleeConnectionId !== this.config.connectionId) {
      if (this.view?.id === call.id) this.finish('Cuộc gọi đã được nhận ở phiên đăng nhập khác.');
      return;
    }
    if (call.state === 'Ended') {
      if (this.view?.id === call.id) this.finish(this.reason(call.reason));
      return;
    }
    if (this.call && this.call.id !== call.id) this.stopMedia();
    if (!caller && this.view?.id !== call.id) { ++this.generation; this.stopMedia(); }
    this.call = call;
    const peerId = caller ? call.calleeId : call.callerId;
    const peerName = (caller ? call.calleeName : call.callerName) || (this.view?.peerId === peerId ? this.view.peerName : '') || 'Người dùng';
    const phase = call.state === 'Ringing' ? caller ? 'outgoing' : 'incoming' : call.state === 'Active' ? 'active' : 'connecting';
    // Không thay UI xin camera của chính phiên này bằng snapshot Ringing cùng version.
    const preparing = this.view?.id === call.id && this.view.phase === 'preparing' && !caller && call.state === 'Ringing';
    this.viewSubject.next({ id: call.id, peerId, peerName, phase: preparing ? 'preparing' : phase,
      message: phase === 'incoming' ? 'Cuộc gọi video đến' : phase === 'outgoing' ? 'Đang đổ chuông…' : phase === 'active' ? 'Đang trong cuộc gọi' : 'Đang kết nối hình ảnh và âm thanh…', connectedAt: call.connectedAt });
    this.startPulse();
    if (call.state === 'Connecting' && this.localSubject.value) {
      this.ensurePeer();
      if (caller && !this.offerStarted) {
        this.offerStarted = true;
        void this.offer().catch(() => { if (this.call?.id === call.id) this.fail('Không tạo được kết nối video. Hãy gọi lại.'); });
      }
    }
  }
  private ensurePeer(): RTCPeerConnection {
    if (this.peer) return this.peer;
    if (!this.config || !this.localSubject.value) throw new Error('Camera chưa sẵn sàng.');
    const peer = new RTCPeerConnection({ iceServers: this.config.iceServers, iceTransportPolicy: this.config.iceTransportPolicy });
    this.peer = peer;
    this.localSubject.value.getTracks().forEach(t => peer.addTrack(t, this.localSubject.value!));
    const remote = new MediaStream();
    peer.ontrack = e => this.zone.run(() => {
      if (this.peer !== peer) return;
      if (!remote.getTracks().some(t => t.id === e.track.id)) remote.addTrack(e.track);
      this.remoteSubject.next(remote);
    });
    peer.onicecandidate = e => {
      if (e.candidate && this.peer === peer && this.call) {
        const id = this.call.id;
        void this.hub.callInvoke('SendVideoCallSignal', id, 'ice', JSON.stringify(e.candidate.toJSON())).catch(() => this.zone.run(() => {
          if (this.call?.id === id && this.peer === peer) this.fail('Không trao đổi được thông tin kết nối. Hãy gọi lại.');
        }));
      }
    };
    peer.onconnectionstatechange = () => this.zone.run(() => {
      if (this.peer !== peer) return;
      if (peer.connectionState === 'connected') { clearTimeout(this.recoveryTimer); clearTimeout(this.connectingTimer); void this.pulse(); }
      if (peer.connectionState === 'failed') this.fail('Không kết nối được hai thiết bị. Mạng này có thể cần máy chủ TURN.');
      if (peer.connectionState === 'disconnected') {
        clearTimeout(this.recoveryTimer);
        this.recoveryTimer = setTimeout(() => this.fail('Mất kết nối video. Hãy gọi lại.'), 10000);
      }
    });
    this.connectingTimer = setTimeout(() => this.zone.run(() => this.fail('Không thiết lập được đường truyền video. Hãy kiểm tra STUN/TURN hoặc thử mạng khác.')), 45000);
    return peer;
  }
  private async offer(): Promise<void> {
    const peer = this.peer!, id = this.call!.id;
    await peer.setLocalDescription(await peer.createOffer());
    if (this.peer === peer) await this.hub.callInvoke('SendVideoCallSignal', id, 'offer', peer.localDescription!.sdp);
  }
  private async receiveSignal(signal: VideoCallSignal): Promise<void> {
    await this.setup;
    if (!this.call || this.call.id !== signal.callId || !this.busy) return;
    const peer = this.ensurePeer();
    if (signal.kind === 'ice') {
      const candidate = JSON.parse(signal.data) as RTCIceCandidateInit;
      if (peer.remoteDescription) await peer.addIceCandidate(candidate);
      else if (this.candidates.length < 128) this.candidates.push(candidate);
      return;
    }
    if ((signal.kind === 'offer') === (this.call.callerId === this.auth.currentUser?.id)) throw new Error('Vai trò SDP không hợp lệ.');
    await peer.setRemoteDescription({ type: signal.kind, sdp: signal.data });
    if (this.peer !== peer) return;
    for (const candidate of this.candidates.splice(0)) { if (this.peer !== peer) return; await peer.addIceCandidate(candidate); }
    if (signal.kind === 'offer') {
      await peer.setLocalDescription(await peer.createAnswer());
      if (this.peer === peer) await this.hub.callInvoke('SendVideoCallSignal', signal.callId, 'answer', peer.localDescription!.sdp);
    }
  }
  private startPulse(): void {
    if (this.timer) return;
    this.zone.runOutsideAngular(() => { this.timer = setInterval(() => void this.zone.run(() => this.pulse()), 5000); });
  }
  private async pulse(): Promise<void> {
    if (!this.call || !this.busy || this.pulsing) return;
    const id = this.call.id; this.pulsing = true;
    try {
      const call = await this.hub.callInvoke<VideoCall>('PulseVideoCall', id, this.peer?.connectionState === 'connected');
      if (this.call?.id === id) this.apply(call);
    } catch { if (this.call?.id === id && this.busy) this.fail('Cuộc gọi không còn kết nối hoặc quyền truy cập đã thay đổi.'); }
    finally { this.pulsing = false; }
  }
  private fail(message: string): void {
    if (!this.busy) return;
    const id = this.call?.id; this.finish(message);
    if (id) void this.hub.callInvoke('EndVideoCall', id, 'failed').catch(() => undefined);
  }
  private stopMedia(): void {
    clearInterval(this.timer); this.timer = undefined;
    clearTimeout(this.connectingTimer); clearTimeout(this.recoveryTimer);
    const peer = this.peer; this.peer = null;
    if (peer) { peer.onicecandidate = null; peer.ontrack = null; peer.onconnectionstatechange = null; peer.close(); }
    this.localSubject.value?.getTracks().forEach(t => t.stop());
    this.remoteSubject.value?.getTracks().forEach(t => t.stop());
    this.localSubject.next(null); this.remoteSubject.next(null);
    this.candidates = []; this.offerStarted = false;
  }
  private finish(message: string): void {
    ++this.generation;
    if (this.view) {
      this.ended.add(this.view.id); if (this.ended.size > 128) this.ended.delete(this.ended.values().next().value!);
      this.viewSubject.next({ ...this.view, phase: 'ended', message });
    }
    this.stopMedia(); this.call = null;
  }
  private reset(): void { ++this.generation; this.stopMedia(); this.call = null; this.viewSubject.next(null); }
  private friendly(error: unknown): string {
    const e = error as { name?: string; message?: string };
    if (e.name === 'NotAllowedError' || e.name === 'SecurityError') return 'Bạn chưa cho phép sử dụng camera/microphone. Hãy cấp quyền trong trình duyệt rồi gọi lại.';
    if (e.name === 'NotFoundError') return 'Không tìm thấy camera hoặc microphone.';
    if (e.name === 'NotReadableError') return 'Camera/microphone đang bận hoặc không mở được. Hãy đóng ứng dụng đang sử dụng thiết bị.';
    return e.message?.split('HubException: ').pop() || 'Chưa bắt đầu được cuộc gọi. Hãy thử lại.';
  }
  private reason(reason: string): string {
    return ({ 'declined': 'Người kia đã từ chối cuộc gọi.', 'cancelled': 'Người gọi đã hủy cuộc gọi.', 'no-answer': 'Không có người trả lời.',
      'connection-lost': 'Cuộc gọi kết thúc do mất kết nối.', 'permission-changed': 'Cuộc gọi kết thúc vì quyền truy cập hoặc tài khoản đã thay đổi.',
      'failed': 'Không thiết lập được cuộc gọi. Hãy kiểm tra camera, mạng hoặc STUN/TURN.' } as { [key: string]: string })[reason] || 'Cuộc gọi đã kết thúc.';
  }
}
