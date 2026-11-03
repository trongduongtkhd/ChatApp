export interface VideoCall {
  id: string; groupId: string; callerId: string; calleeId: string;
  callerName: string; calleeName: string; callerConnectionId: string; calleeConnectionId: string;
  state: 'Ringing' | 'Connecting' | 'Active' | 'Ended'; reason: string;
  version: number; createdAt: number; deadline: number; connectedAt: number;
}
export interface VideoCallSignal { callId: string; kind: 'offer' | 'answer' | 'ice'; data: string; }
export interface VideoCallConfig { connectionId: string; iceServers: RTCIceServer[]; iceTransportPolicy: RTCIceTransportPolicy; }
export type CallPhase = 'preparing' | 'outgoing' | 'incoming' | 'connecting' | 'active' | 'ended';
export interface VideoCallView { id: string; peerId: string; peerName: string; phase: CallPhase; message: string; connectedAt: number; }
