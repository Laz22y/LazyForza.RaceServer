import {afterEach,beforeEach,describe,expect,it,vi} from "vitest";
import {RaceRoom} from "../src/index";

class Socket {
  static OPEN=1;static CLOSING=2;static CLOSED=3;
  readyState=1;attachment:Record<string,unknown>={};messages:{type:string;payload:Record<string,unknown>}[]=[];
  closeCode?:number;holdClose=false;
  serializeAttachment(value:Record<string,unknown>):void {this.attachment=structuredClone(value);}
  deserializeAttachment():Record<string,unknown> {return structuredClone(this.attachment);}
  send(value:string):void {this.messages.push(JSON.parse(value));}
  close(code:number):void {if(this.readyState===3)return;this.closeCode=code;this.readyState=this.holdClose?2:3;}
  wire():WebSocket {return this as unknown as WebSocket;}
}
class State {
  values=new Map<string,unknown>();sockets:Socket[]=[];alarmAt:number|null=null;
  storage={
    get:async <T>(key:string):Promise<T|undefined>=>structuredClone(this.values.get(key)) as T|undefined,
    put:async (key:string|Record<string,unknown>,value?:unknown):Promise<void>=>{
      if(typeof key==="string") this.values.set(key,structuredClone(value));
      else for(const [k,v] of Object.entries(key)) this.values.set(k,structuredClone(v));
    },
    setAlarm:async (at:number):Promise<void>=>{this.alarmAt=at;},
    deleteAlarm:async ():Promise<void>=>{this.alarmAt=null;}
  };
  blockConcurrencyWhile<T>(callback:()=>Promise<T>):Promise<T> {return callback();}
  getWebSockets():WebSocket[] {return this.sockets.filter(socket=>socket.readyState!==3).map(socket=>socket.wire());}
  acceptWebSocket(socket:WebSocket):void {this.sockets.push(socket as unknown as Socket);}
  wire():DurableObjectState {return this as unknown as DurableObjectState;}
}
const OriginalResponse=globalThis.Response;
let now=100000;
beforeEach(()=>{
  now=100000;vi.spyOn(Date,"now").mockImplementation(()=>now);
  vi.stubGlobal("WebSocket",Socket);
  vi.stubGlobal("WebSocketPair",class {0=new Socket();1=new Socket();});
  vi.stubGlobal("Response",class extends OriginalResponse {
    constructor(body:BodyInit|null,init:ResponseInit={}) {
      super(body,init.status===101?{...init,status:200}:init);
      if(init.status===101) {Object.defineProperty(this,"status",{value:101});Object.defineProperty(this,"webSocket",{value:init.webSocket});}
    }
  });
});
afterEach(()=>{vi.restoreAllMocks();vi.unstubAllGlobals();});
function setup(limits:Record<string,number>={}) {
  const state=new State();
  const env={INGRESS_LIMITS:JSON.stringify(limits),PLAYER_PASSWORD:"player-pass",ADMIN_PASSWORD:"admin-pass",
    SERVER_NAME:"Tests",SESSION_NAME:"Tests",MAXIMUM_PARTICIPANTS:"12",TOTAL_RACE_LAPS:"10"} as ConstructorParameters<typeof RaceRoom>[1];
  return {state,env,room:new RaceRoom(state.wire(),env)};
}
function request(path:string,body?:unknown):Request {
  return new Request("https://race.test"+path,body===undefined?{headers:{Upgrade:"websocket"}}:
    {method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(body)});
}
async function connect(room:RaceRoom,state:State):Promise<Socket> {
  expect((await room.fetch(request("/ws"))).status).toBe(101);return state.sockets[state.sockets.length-1];
}
async function send(room:RaceRoom,socket:Socket,type:string,payload:unknown={}):Promise<void> {
  await room.webSocketMessage(socket.wire(),JSON.stringify({protocolVersion:2,type,sequence:1,payload}));
}
function login(name:string,isObserver=false) {return {password:"player-pass",displayName:name,themeColor:"#336699",teamId:"team-1",clientVersion:"test",isObserver};}
function message(socket:Socket,type:string) {return socket.messages.slice().reverse().find(item=>item.type===type)?.payload;}

describe("ingress routes",()=>{
  it("returns HTTP 429 with retry hints, retains failures across reconstruction and recovers",async()=>{
    let {room,state,env}=setup({loginFailureLimit:1,loginFailureWindowSeconds:2});
    expect((await room.fetch(request("/api/admin/login",{password:"wrong"}))).status).toBe(401);
    room=new RaceRoom(state.wire(),env);
    const denied=await room.fetch(request("/api/admin/login",{password:"admin-pass"}));
    expect(denied.status).toBe(429);expect(denied.headers.get("Retry-After")).toBe("2");
    expect(await denied.json()).toMatchObject({code:"rateLimited",retryAfterSeconds:2});
    now+=2000;
    expect((await room.fetch(request("/api/admin/login",{password:"admin-pass"}))).status).toBe(200);
  });
  it("rejects excess upgrades, retains closing sockets in quota, and cleans deadlines after wakeup",async()=>{
    let {room,state,env}=setup({maximumUnauthenticatedConnections:1,maximumUnauthenticatedPerSource:1,loginTimeoutSeconds:1});
    const pending=await connect(room,state);pending.holdClose=true;
    expect(state.alarmAt).toBe(now+1000);
    expect((await room.fetch(request("/ws"))).status).toBe(429);
    room=new RaceRoom(state.wire(),env);now+=1000;await room.alarm();
    expect(pending.closeCode).toBe(1008);expect(message(pending,"loginRejected")?.code).toBe("loginTimeout");
    expect((await room.fetch(request("/ws"))).status).toBe(429);
    pending.readyState=3;await room.webSocketClose(pending.wire(),1008,"timeout");
    await connect(room,state);
  });
  it("allows twelve drivers and twelve observers behind one exit with only one pending slot",async()=>{
    const {room,state}=setup({maximumUnauthenticatedConnections:1,maximumUnauthenticatedPerSource:1});
    for(let i=0;i<24;i++) {
      const socket=await connect(room,state);
      await send(room,socket,"login",{...login(`normal-${i}`,i>=12),teamId:i<6?"team-1":"team-2"});
      expect(message(socket,"loginAccepted"),JSON.stringify(socket.messages)).toBeTruthy();
    }
    expect(state.sockets.filter(socket=>socket.readyState===1)).toHaveLength(24);
  });
  it("isolates login failures and sends retry errors before closing",async()=>{
    const {room,state}=setup({loginFailureLimit:1});
    const wrong=await connect(room,state);await send(room,wrong,"login",{...login("bad"),password:"wrong"});
    expect(wrong.closeCode).toBe(1008);
    const blocked=await connect(room,state);await send(room,blocked,"login",login("bad"));
    expect(blocked.closeCode).toBe(1013);expect(message(blocked,"loginRejected")).toMatchObject({code:"rateLimited",retryAfterSeconds:60});
    const normal=await connect(room,state);await send(room,normal,"login",login("normal"));
    expect(message(normal,"loginAccepted")).toBeTruthy();
  });
  it("preserves each connection budget after wakeup and closes only the flooding socket",async()=>{
    let {room,state,env}=setup({messageBurst:2,messagesPerSecond:1});
    const noisy=await connect(room,state),normal=await connect(room,state);
    await send(room,noisy,"login",login("noisy"));await send(room,normal,"login",login("normal"));
    await send(room,noisy,"ping");room=new RaceRoom(state.wire(),env);
    await send(room,noisy,"ping");
    expect(noisy.closeCode).toBe(1013);expect(message(noisy,"error")?.code).toBe("rateLimited");
    await room.webSocketClose(noisy.wire(),1013,"rateLimited");
    await send(room,normal,"ping");expect(message(normal,"pong")).toBeTruthy();expect(normal.readyState).toBe(1);
    now+=1000;await send(room,normal,"ping");expect(normal.readyState).toBe(1);
  });
  it("enforces byte budget before parsing invalid payload and releases an early disconnect",async()=>{
    const {room,state}=setup({byteBurst:512,bytesPerSecond:512,maximumUnauthenticatedConnections:1,maximumUnauthenticatedPerSource:1});
    const early=await connect(room,state);early.close(1000);await room.webSocketClose(early.wire(),1000,"early");
    const oversized=await connect(room,state);await room.webSocketMessage(oversized.wire(),"x".repeat(513));
    expect(oversized.closeCode).toBe(1013);expect(message(oversized,"loginRejected")?.code).toBe("rateLimited");
    await connect(room,state);
  });
  it("bounds unknown-length HTTP bodies and releases verification slots after a read timeout",async()=>{
    const {room}=setup({maximumConcurrentLogins:1,loginTimeoutSeconds:1});
    const huge=request("/api/admin/login",{password:"x".repeat(65537)});
    expect((await room.fetch(huge)).status).toBe(413);
    const stream=new ReadableStream<Uint8Array>({start(){}});
    const init={method:"POST",body:stream,duplex:"half"} as RequestInit;
    const slow=await room.fetch(new Request("https://race.test/api/admin/login",init));
    expect(slow.status).toBe(408);
    expect((await room.fetch(request("/api/admin/login",{password:"admin-pass"}))).status).toBe(200);
  });
  it("closes malformed, binary and oversized messages with policy-specific codes",async()=>{
    const {room,state}=setup();
    const malformed=await connect(room,state);await room.webSocketMessage(malformed.wire(),"{");expect(malformed.closeCode).toBe(1008);
    const binary=await connect(room,state);await room.webSocketMessage(binary.wire(),new ArrayBuffer(3));expect(binary.closeCode).toBe(1008);
    const huge=await connect(room,state);await room.webSocketMessage(huge.wire(),"x".repeat(65537));expect(huge.closeCode).toBe(1009);
  });
  it("releases resources on socket errors",async()=>{
    const {room,state}=setup({maximumUnauthenticatedConnections:1,maximumUnauthenticatedPerSource:1});
    const failed=await connect(room,state);await room.webSocketError(failed.wire());
    expect(failed.closeCode).toBe(1011);await connect(room,state);
  });
});
