"""Отправка RCON-команд на тестовый сервер Rust через WebSocket."""
import sys, json, asyncio
import websockets

async def main():
    cmds = sys.argv[1:]
    uri = "ws://127.0.0.1:28116/Octavian_2026_ChangeMe"
    async with websockets.connect(uri) as ws:
        for i, c in enumerate(cmds, 1):
            await ws.send(json.dumps({"Identifier": i, "Message": c, "Name": "rcon_send"}))
            try:
                resp = await asyncio.wait_for(ws.recv(), timeout=10)
                print(c, "=>", resp[:400])
            except asyncio.TimeoutError:
                print(c, "=> (timeout)")

asyncio.run(main())
