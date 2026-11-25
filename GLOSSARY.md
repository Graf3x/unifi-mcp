# unifi-mcp

An MCP server that lets Claude read, and with the owner's leave change, one home UniFi Network console. The public side acts as an OAuth authorization server so Claude can connect as a custom connector.

## Console

**Site**:
One network managed by the console. It can be named by its id, its name, or its internal name. An id always wins; a name or internal name that more than one site has names none of them. A write looks a name up again each time, since a renamed site's old name can pass to another.
_Avoid_: network (a site holds many networks), location

**Integration API**:
The console's official, documented API. Everything the server changes goes through it.

**Internal API**:
The undocumented API behind the console's own web interface. The server only reads from it, slowly and with short-lived caching, because it answers what the Integration API cannot.
_Avoid_: private API, legacy API

**Device reading**:
What the server makes of one device as the console reports it at one moment: whether it is online, its health, firmware and uplink, its ports and what is plugged into each. The summary, the device's own answer and its view all show the same reading.

**Online**:
The console reports the device as connected. Every other state (upgrading, adopting, heartbeat missed, …) counts as not online.
_Avoid_: up (a port is up), connected (a client is connected)

**Port flag**:
A problem a switch port shows: errors, flapping, half duplex or a slow link. A slow link depends on what the port can do at most, so only the device's own answer can raise it; the summary flags the rest.
_Avoid_: alert, warning

## Answers

**Step**:
One part of a composite tool's answer that reads from the console. When a step fails the answer says so and the rest still runs; when a read the tool cannot do without fails, there is no answer, only the reason.
_Avoid_: stage, check (a check is the setup page's)

## Access

**Listener**:
One way into the server, with its own callers and its own short list of what it serves: the public side (the connector, reached through the tunnel), the tailnet, the LAN, and the setup page. A request that doesn't come in through a known listener is refused.
_Avoid_: socket, port, side (except "the public side")

**Owner**:
The one person allowed to use the server: a listed Google address, or whoever holds the sign-in code.
_Avoid_: user, admin, account

**Write approval**:
The owner's yes to one change to the network, given for that change alone: on a confirmation form where the client can show one, otherwise by the model repeating the call with the tool's own name. A yes on the form holds once, for that exact request and arguments, and lapses within minutes; the fallback has no such binding, since the model gives it in the same call it makes. Not the same as consent, which lets a client connect at all.
_Avoid_: consent, permission, confirmation (except the form itself)

**Admission**:
The decision that an identity is the owner under the current settings. It is made again each time the identity is used, so changing the allowed addresses or the sign-in method ends access for anyone who no longer qualifies.
_Avoid_: authorization, allow-listing

**Client**:
An application that asks for access on the owner's behalf, such as Claude. It is identified by a registration or by a published metadata document.
_Avoid_: app, connector (the connector is this server's public side)

**Registration**:
A client that introduced itself to this server and was given an id. Registering alone grants nothing.
_Avoid_: app registration, account

**Metadata document**:
A client's description of itself, published at the address it uses as its id. Its publisher vouches for it, so the owner is shown the publisher's host, not the name the document gives.
_Avoid_: manifest, client file

**Consent**:
The owner's standing approval of one client. It is given once and holds until it is withdrawn or its family is revoked.
_Avoid_: authorization, approval (a write approval is a different thing)

**Family**:
Every refresh token descended from one consent. A family is revoked as a whole, never token by token.
_Avoid_: chain, session

**Grant**:
A consent together with its family: what lets a client keep getting access tokens without the owner signing in again.
_Avoid_: connection, session

**Grant ledger**:
The record of every grant, and the rules that decide whether a grant still stands.

**Authorization code**:
A single-use proof that the owner just approved a client. The client exchanges it for its first tokens, which start in the family of the consent that issued the code, and only while that consent stands.

**Refresh token**:
A secret the client trades for a new access token and a new refresh token. Each one can be used once.

**Reuse**:
A refresh token presented after it was already used. It means the token leaked, so the whole family is revoked.
_Avoid_: replay

**Disconnect**:
The owner withdrawing a client's consent on the Connections page. It revokes the family, and the client has to be approved again.
_Avoid_: unlink, remove
