# unifi-mcp

An MCP server that lets Claude read, and with the owner's leave change, one home UniFi Network console. The public side acts as an OAuth authorization server so Claude can connect as a custom connector.

## Access

**Owner**:
The one person allowed to use the server: a listed Google address, or whoever holds the sign-in code.
_Avoid_: user, admin, account

**Client**:
An application that asks for access on the owner's behalf, such as Claude. It is identified by a registration or by a published metadata document.
_Avoid_: app, connector (the connector is this server's public side)

**Consent**:
The owner's standing approval of one client. It is given once and holds until it is withdrawn or its family is revoked.
_Avoid_: authorization, approval

**Family**:
Every refresh token descended from one consent. A family is revoked as a whole, never token by token.
_Avoid_: chain, session

**Grant**:
A consent together with its family: what lets a client keep getting access tokens without the owner signing in again.
_Avoid_: connection, session

**Grant ledger**:
The record of every grant, and the rules that decide whether a grant still stands.

**Authorization code**:
A single-use proof that the owner just approved a client. The client exchanges it for its first tokens.

**Refresh token**:
A secret the client trades for a new access token and a new refresh token. Each one can be used once.

**Reuse**:
A refresh token presented after it was already used. It means the token leaked, so the whole family is revoked.
_Avoid_: replay

**Disconnect**:
The owner withdrawing a client's consent on the Connections page. It revokes the family, and the client has to be approved again.
_Avoid_: unlink, remove
