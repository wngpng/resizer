﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ImageResizer.Plugins.LicenseVerifier.Tests
{
    public class LicenseVerifierTests
    {
        [Fact]
        public void Test_License_Verifier()
        {
            var exp = BigInteger.Parse("65537");
            var mod = BigInteger.Parse("28178177427582259905122756905913963624440517746414712044433894631438407111916149031583287058323879921298234454158166031934230083094710974550125942791690254427377300877691173542319534371793100994953897137837772694304619234054383162641475011138179669415510521009673718000682851222831185756777382795378538121010194881849505437499638792289283538921706236004391184253166867653735050981736002298838523242717690667046044130539971131293603078008447972889271580670305162199959939004819206804246872436611558871928921860176200657026263241409488257640191893499783065332541392967986495144643652353104461436623253327708136399114561");

            StringBuilder sb = new StringBuilder();
            var blob = LicenseBlob.Deserialize("localhost:RG9tYWluOiBsb2NhbGhvc3QKT3duZXI6IEV2ZXJ5b25lCklzc3VlZDogMjAxNS0wMy0yOFQwMDoyNzozMloKRmVhdHVyZXM6IFI0RWxpdGUgUjRDcmVhdGl2ZSBSNFBlcmZvcm1hbmNlCg==:3mA7/keJLfchZz1AEUHeH18nAvNsHbX7D6RCRbnMDsJpDGPfR0mS70nZmbOyjpCvTVWhT00rDRcyEvUfktbjZl9IEFW8h6vMhee/N+iDnLTR+jGRKPHUyZHfiSlEGzfy5MpVPNJIj5p8pyITZxtxNGC2FTA7NYWJXrUDR6A2WzWvLk7GTmtBlh6UnDUKRsi/1VgzKIJgX3MX/e6CO97yOwW/fk65UAVypoWGZObscT29I3yR7Q6UU1SHX3Zi8dT6PTXVj1p0Tm68N7pCX4iof42xJ1FrV+HdsTHsbQVpuFSZUptWtgy1U9ieHENmgBcZdUnmJyoKvx6hX3MUepk12A==");
            var valid = new LicenseValidator().Validate(blob, new[] { new RSADecryptPublic(mod, exp) }, sb);

            Assert.True(valid, sb.ToString());
        }
    }
}
