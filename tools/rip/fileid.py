"""Unity script fileID = first 4 bytes (LE int32) of MD4('s\0\0\0' + namespace + classname)."""
import struct, sys
def md4(msg):
    def F(x,y,z): return (x&y)|(~x&z)
    def G(x,y,z): return (x&y)|(x&z)|(y&z)
    def H(x,y,z): return x^y^z
    L=lambda x,n:((x<<n)|(x>>(32-n)))&0xffffffff
    ml=len(msg)*8; msg+=b'\x80'; msg+=b'\0'*((56-len(msg)%64)%64); msg+=struct.pack('<Q',ml)
    a,b,c,d=0x67452301,0xefcdab89,0x98badcfe,0x10325476
    for i in range(0,len(msg),64):
        X=struct.unpack('<16I',msg[i:i+64]); A,B,C,D=a,b,c,d
        for k,s in zip(range(16),[3,7,11,19]*4):
            A=L((A+F(B,C,D)+X[k])&0xffffffff,s); A,B,C,D=D,A,B,C
        for k,s in zip([0,4,8,12,1,5,9,13,2,6,10,14,3,7,11,15],[3,5,9,13]*4):
            A=L((A+G(B,C,D)+X[k]+0x5a827999)&0xffffffff,s); A,B,C,D=D,A,B,C
        for k,s in zip([0,8,4,12,2,10,6,14,1,9,5,13,3,11,7,15],[3,9,11,15]*4):
            A=L((A+H(B,C,D)+X[k]+0x6ed9eba1)&0xffffffff,s); A,B,C,D=D,A,B,C
        a,b,c,d=(a+A)&0xffffffff,(b+B)&0xffffffff,(c+C)&0xffffffff,(d+D)&0xffffffff
    return struct.pack('<4I',a,b,c,d)
for full in sys.argv[1:]:
    ns,_,n=full.rpartition('.')
    print(struct.unpack('<i',md4(b's\0\0\0'+ns.encode()+n.encode())[:4])[0], full)
