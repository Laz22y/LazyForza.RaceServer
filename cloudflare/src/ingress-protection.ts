export interface IngressOptions {
  loginFailureLimit: number; sourceLoginFailureLimit: number; loginFailureWindowSeconds: number;
  maximumConcurrentLogins: number; maximumFailureBuckets: number;
  maximumUnauthenticatedConnections: number; maximumUnauthenticatedPerSource: number; loginTimeoutSeconds: number;
  messagesPerSecond: number; messageBurst: number; bytesPerSecond: number; byteBurst: number;
}
export const defaultIngressOptions: IngressOptions = {
  loginFailureLimit: 5, sourceLoginFailureLimit: 120, loginFailureWindowSeconds: 60,
  maximumConcurrentLogins: 32, maximumFailureBuckets: 4096,
  maximumUnauthenticatedConnections: 64, maximumUnauthenticatedPerSource: 32, loginTimeoutSeconds: 12,
  messagesPerSecond: 60, messageBurst: 120, bytesPerSecond: 131072, byteBurst: 262144
};
export function ingressOptions(json?: string): IngressOptions {
  const options = {...defaultIngressOptions, ...JSON.parse(json ?? "{}")} as IngressOptions;
  if (Object.values(options).some(value => !Number.isSafeInteger(value) || value < 1) ||
    options.sourceLoginFailureLimit < options.loginFailureLimit || options.loginFailureWindowSeconds > 3600 ||
    options.maximumConcurrentLogins > 1024 || options.maximumFailureBuckets < 2 || options.maximumFailureBuckets > 65536 ||
    options.maximumUnauthenticatedConnections > 1024 || options.maximumUnauthenticatedPerSource > options.maximumUnauthenticatedConnections ||
    options.loginTimeoutSeconds > 120) throw new Error("Invalid INGRESS_LIMITS configuration.");
  return options;
}
interface Bucket { expires: number; count: number }
export interface LoginTicket { keys: string[]; buckets: Bucket[]; completed?: boolean }
export class LoginFailureBudget {
  private buckets = new Map<string, Bucket>();
  private active = 0;
  constructor(readonly options: IngressOptions, stored: [string, Bucket][] = []) {
    this.buckets = new Map(stored);
  }
  serialize(): [string, Bucket][] { return [...this.buckets]; }
  begin(source: string, channel: string, identity: string, now: number): {ticket?: LoginTicket; retryAfterSeconds: number} {
    for (const [key,bucket] of this.buckets) if(bucket.expires <= now) this.buckets.delete(key);
    if(this.active >= this.options.maximumConcurrentLogins) return {retryAfterSeconds:1};
    const keys = [channel+":"+source,channel+":"+source+":"+identity];
    const limits = [this.options.sourceLoginFailureLimit,this.options.loginFailureLimit];
    for(let i=0;i<keys.length;i++) {
      const existing = this.buckets.get(keys[i]);
      if(existing && existing.count >= limits[i]) return {retryAfterSeconds:seconds(existing.expires-now)};
    }
    if(this.buckets.size + keys.filter(key=>!this.buckets.has(key)).length > this.options.maximumFailureBuckets)
      return {retryAfterSeconds:seconds(Math.min(...[...this.buckets.values()].map(bucket=>bucket.expires))-now)};
    const buckets = keys.map(key=>{
      let bucket=this.buckets.get(key);
      if(!bucket) this.buckets.set(key,bucket={expires:now+this.options.loginFailureWindowSeconds*1000,count:0});
      bucket.count++;
      return bucket;
    });
    this.active++;
    return {ticket:{keys,buckets},retryAfterSeconds:1};
  }
  complete(ticket: LoginTicket, valid: boolean): void {
    if(ticket.completed) return;
    ticket.completed=true;
    this.active--;
    if(valid) ticket.buckets.forEach((bucket,i)=>{
      bucket.count--;
      if(bucket.count===0 && this.buckets.get(ticket.keys[i])===bucket) this.buckets.delete(ticket.keys[i]);
    });
  }
}
export interface MessageBudgetState { messages: number; bytes: number; at: number }
export function consumeMessage(options: IngressOptions, previous: MessageBudgetState | undefined, byteCount: number, now: number):
  {state: MessageBudgetState; allowed: boolean; retryAfterSeconds: number} {
  const elapsed = Math.max(0,now-(previous?.at??now))/1000;
  const messages = Math.min(options.messageBurst,(previous?.messages??options.messageBurst)+elapsed*options.messagesPerSecond);
  const bytes = Math.min(options.byteBurst,(previous?.bytes??options.byteBurst)+elapsed*options.bytesPerSecond);
  const allowed=messages>=1&&bytes>=byteCount;
  return {state:{messages:messages-(allowed?1:0),bytes:bytes-(allowed?byteCount:0),at:now},allowed,
    retryAfterSeconds:Math.max(1,Math.ceil(Math.max((1-messages)/options.messagesPerSecond,(byteCount-bytes)/options.bytesPerSecond)))};
}
export function connectionAllowed(options: IngressOptions, pending: {source?: string; loginDeadline?: number; participantId?: string; closed?: boolean}[], source: string): boolean {
  const live=pending.filter(item=>!item.participantId&&!item.closed);
  return live.length<options.maximumUnauthenticatedConnections&&live.filter(item=>(item.source??"unknown")===source).length<options.maximumUnauthenticatedPerSource;
}
export async function loginIdentity(name?: string, resumeToken?: string | null): Promise<string> {
  const input=resumeToken?.trim()?resumeToken:(name??"").trim().slice(0,20).toUpperCase();
  return [...new Uint8Array(await crypto.subtle.digest("SHA-256",new TextEncoder().encode(input)))].map(x=>x.toString(16).padStart(2,"0")).join("");
}
export function transportSource(request: Request): string {
  const peer=request.headers.get("CF-Connecting-IP");
  // Worker subrequests can alter x-real-ip, which changes CF-Connecting-IP in the same zone.
  if(!request.cf || request.headers.has("CF-Worker") || !peer || !/^[0-9a-f:.]{3,45}$/i.test(peer)) return "unknown";
  return peer.toLowerCase();
}
function seconds(ms:number):number { return Math.max(1,Math.ceil(ms/1000)); }
