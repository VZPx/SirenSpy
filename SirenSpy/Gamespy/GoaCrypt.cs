namespace SirenSpy.Gamespy
{
	// GameSpy server browser stream cipher (sb_crypt.c "GOACrypt"), used to encrypt server lists sent to the game.
	public class GoaCrypt
	{
		private readonly byte[] _cards = new byte[256];
		private byte _rotor, _ratchet, _avalanche, _lastPlain, _lastCipher;

		public GoaCrypt(byte[] key)
		{
			for (int i = 0; i < 256; i++) _cards[i] = (byte)i;

			byte rsum = 0;
			int keypos = 0;
			for (int i = 255; i >= 0; i--)
			{
				int swap = KeyRand(i, key, ref rsum, ref keypos);
				(_cards[i], _cards[swap]) = (_cards[swap], _cards[i]);
			}

			_rotor = _cards[1];
			_ratchet = _cards[3];
			_avalanche = _cards[5];
			_lastPlain = _cards[7];
			_lastCipher = _cards[rsum];
		}

		private int KeyRand(int limit, byte[] key, ref byte rsum, ref int keypos)
		{
			if (limit == 0) return 0;

			int retryLimiter = 0, mask = 1;
			while (mask < limit) mask = (mask << 1) + 1;

			int u;
			do
			{
				rsum = (byte)(_cards[rsum] + key[keypos++]);
				if (keypos >= key.Length)
				{
					keypos = 0;
					rsum = (byte)(rsum + key.Length);
				}
				u = mask & rsum;
				if (++retryLimiter > 11) u %= limit;
			} while (u > limit);
			return u;
		}

		public byte EncryptByte(byte b)
		{
			_ratchet = (byte)(_ratchet + _cards[_rotor++]);
			byte swapTemp = _cards[_lastCipher];
			_cards[_lastCipher] = _cards[_ratchet];
			_cards[_ratchet] = _cards[_lastPlain];
			_cards[_lastPlain] = _cards[_rotor];
			_cards[_rotor] = swapTemp;
			_avalanche = (byte)(_avalanche + _cards[swapTemp]);

			_lastCipher = (byte)(b
				^ _cards[(byte)(_cards[_avalanche] + _cards[_rotor])]
				^ _cards[_cards[(byte)(_cards[_lastPlain] + _cards[_lastCipher] + _cards[_ratchet])]]);
			_lastPlain = b;
			return _lastCipher;
		}

		public byte[] Encrypt(byte[] data)
		{
			var output = new byte[data.Length];
			for (int i = 0; i < data.Length; i++) output[i] = EncryptByte(data[i]);
			return output;
		}

		// Server list key: the client's 8 byte challenge mixed with the secret key and our challenge (sb_serverlist.c InitCryptKey)
		public static GoaCrypt ForServerList(byte[] clientChallenge, string secretKey, byte[] serverChallenge)
		{
			var challenge = (byte[])clientChallenge.Clone();
			for (int i = 0; i < serverChallenge.Length; i++)
			{
				int index = (i * secretKey[i % secretKey.Length]) % challenge.Length;
				challenge[index] ^= (byte)(challenge[i % challenge.Length] ^ serverChallenge[i]);
			}
			return new GoaCrypt(challenge);
		}
	}
}
