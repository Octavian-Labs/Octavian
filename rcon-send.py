# RCON-отправка команд на тест-сервер: python rcon-send.py "cmd1" "cmd2" ...
import sys, json, asyncio, websockets

async def main():
    async with websockets.connect("ws://127.0.0.1:28116/Octavian_2026_ChangeMe") as ws:
        for i, cmd in enumerate(sys.argv[1:], 1):
            await ws.send(json.dumps({"Identifier": i, "Message": cmd, "Name": "OctaRSM"}))
            try:
                resp = json.loads(await asyncio.wait_for(ws.recv(), timeout=10))
                print(f"### {cmd}\n{resp.get('Message', '')}\n")
            except asyncio.TimeoutError:
                print(f"### {cmd}\n[timeout]\n")

asyncio.run(main())
