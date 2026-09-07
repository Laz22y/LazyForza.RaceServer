import {describe,it,expect} from "vitest";
import {LoginFailureBudget,defaultIngressOptions as defaults,consumeMessage,connectionAllowed,transportSource,ingressOptions} from "../src/ingress-protection";
describe("ingress budgets",()=>{
  it("recovers from failures without extending cooldown and persists through reconstruction",()=>{
    let guard=new LoginFailureBudget(defaults);
    for(let i=0;i<5;i++) guard.complete(guard.begin("nat","player","alice",0).ticket!,false);
    guard=new LoginFailureBudget(defaults,JSON.parse(JSON.stringify(guard.serialize())));
    expect(guard.begin("nat","player","alice",0).retryAfterSeconds).toBe(60);
    expect(guard.begin("nat","player","alice",59000)).toEqual({retryAfterSeconds:1});
    expect(guard.begin("nat","player","alice",60000).ticket).toBeTruthy();
  });
  it("isolates players and administrators sharing one exit and refunds successful logins",()=>{
    const guard=new LoginFailureBudget(defaults);
    for(let i=0;i<5;i++) guard.complete(guard.begin("nat","player","bad",0).ticket!,false);
    for(let round=0;round<10;round++) for(let i=0;i<24;i++) {
      const ticket=guard.begin("nat","player",`player-${i}`,0).ticket;
      expect(ticket).toBeTruthy();guard.complete(ticket!,true);
    }
    expect(guard.begin("nat","admin","admin",0).ticket).toBeTruthy();
  });
  it("bounds rotating identities and simultaneous verification with idempotent release",()=>{
    const guard=new LoginFailureBudget({...defaults,loginFailureLimit:2,sourceLoginFailureLimit:3,maximumConcurrentLogins:2});
    const a=guard.begin("nat","player","a",0).ticket!,b=guard.begin("nat","player","b",0).ticket!;
    expect(guard.begin("other","player","c",0).ticket).toBeUndefined();
    guard.complete(a,true);guard.complete(a,false);guard.complete(b,false);
    for(const key of ["c","d"]) guard.complete(guard.begin("nat","player",key,0).ticket!,false);
    expect(guard.begin("nat","player","rotated",0).ticket).toBeUndefined();
    expect(guard.begin("other","player","normal",0).ticket).toBeTruthy();
  });
  it("bounds failure table and expires entries",()=>{
    const guard=new LoginFailureBudget({...defaults,maximumFailureBuckets:2});
    guard.complete(guard.begin("a","player","one",0).ticket!,false);
    expect(guard.begin("b","player","two",0).ticket).toBeUndefined();
    expect(guard.begin("b","player","two",60000).ticket).toBeTruthy();
  });
  it("counts unauthenticated sockets only and releases closed/authenticated slots",()=>{
    const options={...defaults,maximumUnauthenticatedConnections:3,maximumUnauthenticatedPerSource:2};
    const sockets=[{source:"nat"},{source:"nat"},{source:"other"}];
    expect(connectionAllowed(options,sockets,"nat")).toBe(false);
    expect(connectionAllowed(options,[{...sockets[0],participantId:"driver"},sockets[1],sockets[2]],"nat")).toBe(true);
    expect(connectionAllowed(options,[{...sockets[0],closed:true},sockets[1],sockets[2]],"nat")).toBe(true);
  });
  it("budgets messages and bytes per connection and preserves remaining allowance after hibernation",()=>{
    const options={...defaults,messageBurst:2,messagesPerSecond:2,byteBurst:100,bytesPerSecond:100};
    let result=consumeMessage(options,undefined,40,0);
    result=consumeMessage(options,result.state,40,0);
    result=consumeMessage(options,JSON.parse(JSON.stringify(result.state)),1,0);
    expect(result.allowed).toBe(false);expect(result.retryAfterSeconds).toBe(1);
    expect(consumeMessage(options,undefined,100,0).allowed).toBe(true);
    expect(consumeMessage(options,result.state,100,1000).allowed).toBe(true);
  });
  it("ignores spoofed proxy headers and Worker subrequests",()=>{
    const request=new Request("https://race.test/ws",{headers:{"CF-Connecting-IP":"198.51.100.9","X-Forwarded-For":"203.0.113.9"}});
    expect(transportSource(request)).toBe("unknown");
    Object.defineProperty(request,"cf",{value:{colo:"TEST"}});
    expect(transportSource(request)).toBe("198.51.100.9");
    request.headers.set("CF-Worker","other.test");
    expect(transportSource(request)).toBe("unknown");
  });
  it("rejects invalid configuration",()=>{expect(()=>ingressOptions('{"loginFailureLimit":0}')).toThrow();});
});
