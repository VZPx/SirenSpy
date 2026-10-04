# SirenSpy (PS3/RPCS3)

**State:** 🟩 Working

**Status:** Everything seems to be implemented (Except WBID)

Private C# Gamespy server – made specifically for *Gotham City Impostors*.  
*AI was used for translating Unispy python code to C# (And for some missing endpoints)*

> ⚠️ Only tested using **RPCS3**.

> ⚠️ Matchmaking has only been tested with 1 player. (Need to test with other RPCS3/PS3 players)

---

## Setup

**Step 1:** In RPCS3, go to:  
`RPCS3 → Config → Network → DNS → 127.0.0.1`

(Make sure you're connected with RPCN)

Then go to IP/Host switches and insert the following:
`siren.hydra.agoragames.com=127.0.0.1&&sirenps3.api.gamespy.net=127.0.0.1&&sirenps3.available.gamespy.com=127.0.0.1&&sirenps3.master.gamespy.com=127.0.0.1&&natneg1.gamespy.com=127.0.0.1&&natneg2.gamespy.com=127.0.0.1&&natneg3.gamespy.com=127.0.0.1&&sirenps3.ms19.gamespy.com=127.0.0.1` 

**Step 2:** Generate .ELF File:  
`RPCS3 → Utilities → Decrypt PS3 Binaries → Eboot.bin`
> Eboot.bin is located in Gotham City Impostors → USDIR directory 

**Step 3:** Drag .ELF file to Hex Editor and replace all:  
`"https://"` with `"http://`

Replace Public Key
`BF05D63E93751AD4A59A4A7389CF0BE8A22CCDEEA1E7F12C062D6E194472EFDA5184CCECEB4FBADF5EB1D7ABFE91181453972AA971F624AF9BA8F0F82E2869FB7D44BDE8D56EE50977898F3FEE75869622C4981F07506248BD3D092E8EA05C12B2FA37881176084C8F8B8756C4722CDC57D2AD28ACD3AD85934FB48D6B2D2027`

To Unispy Key

`aefb5064bbd1eb632fa8d57aab1c49366ce0ee3161cbef19f2b7971b63b811790ecbf6a47b34c55f65a0766b40c261c5d69c394cd320842dd2bccba883d30eae8fdba5d03b21b09bfc600dcb30b1b2f3fbe8077630b006dcb54c4254f14891762f72e7bbfe743eb8baf65f9e8c8d11ebe46f6b59e986b4c394cfbc2c8606e29f`

Replace Exponent Key (should be right below the Unispy key)

`010001` to `000001`


> 🛠️ Important: Make sure to add a byte count of 1 for each string replaced so the file size stays the same. The bytes should be added as padding. If done right you should get past the loading profile screen. (If not make sure you added the remaining bytes to a padding section).

**Final Step:** Boot modified .ELF to run Gotham City Impostors, and run SirenSpy along with it.


## Thanks
Unispy - Used a lot of their SDK for matchmaking and for authentication for gamespy services.
