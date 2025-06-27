using Newtonsoft.Json;

namespace UmamusumeResponseAnalyzer.Entities
{
    public class GradeRank
    {
        /// <summary>
        /// 不同等级的马所需的评价点
        /// </summary>
        public static List<GradeRank> GradeToRank { get; private set; } = JsonConvert.DeserializeObject<List<GradeRank>>("[{\"id\":1,\"min_value\":0,\"max_value\":299},{\"id\":2,\"min_value\":300,\"max_value\":599},{\"id\":3,\"min_value\":600,\"max_value\":899},{\"id\":4,\"min_value\":900,\"max_value\":1299},{\"id\":5,\"min_value\":1300,\"max_value\":1799},{\"id\":6,\"min_value\":1800,\"max_value\":2299},{\"id\":7,\"min_value\":2300,\"max_value\":2899},{\"id\":8,\"min_value\":2900,\"max_value\":3499},{\"id\":9,\"min_value\":3500,\"max_value\":4899},{\"id\":10,\"min_value\":4900,\"max_value\":6499},{\"id\":11,\"min_value\":6500,\"max_value\":8199},{\"id\":12,\"min_value\":8200,\"max_value\":9999},{\"id\":13,\"min_value\":10000,\"max_value\":12099},{\"id\":14,\"min_value\":12100,\"max_value\":14499},{\"id\":15,\"min_value\":14500,\"max_value\":15899},{\"id\":16,\"min_value\":15900,\"max_value\":17499},{\"id\":17,\"min_value\":17500,\"max_value\":19199},{\"id\":18,\"min_value\":19200,\"max_value\":19599},{\"id\":19,\"min_value\":19600,\"max_value\":19999},{\"id\":20,\"min_value\":20000,\"max_value\":20399},{\"id\":21,\"min_value\":20400,\"max_value\":20799},{\"id\":22,\"min_value\":20800,\"max_value\":21199},{\"id\":23,\"min_value\":21200,\"max_value\":21599},{\"id\":24,\"min_value\":21600,\"max_value\":22099},{\"id\":25,\"min_value\":22100,\"max_value\":22499},{\"id\":26,\"min_value\":22500,\"max_value\":22999},{\"id\":27,\"min_value\":23000,\"max_value\":23399},{\"id\":28,\"min_value\":23400,\"max_value\":23899},{\"id\":29,\"min_value\":23900,\"max_value\":24299},{\"id\":30,\"min_value\":24300,\"max_value\":24799},{\"id\":31,\"min_value\":24800,\"max_value\":25299},{\"id\":32,\"min_value\":25300,\"max_value\":25799},{\"id\":33,\"min_value\":25800,\"max_value\":26299},{\"id\":34,\"min_value\":26300,\"max_value\":26799},{\"id\":35,\"min_value\":26800,\"max_value\":27299},{\"id\":36,\"min_value\":27300,\"max_value\":27799},{\"id\":37,\"min_value\":27800,\"max_value\":28299},{\"id\":38,\"min_value\":28300,\"max_value\":28799},{\"id\":39,\"min_value\":28800,\"max_value\":29399},{\"id\":40,\"min_value\":29400,\"max_value\":29899},{\"id\":41,\"min_value\":29900,\"max_value\":30399},{\"id\":42,\"min_value\":30400,\"max_value\":30999},{\"id\":43,\"min_value\":31000,\"max_value\":31499},{\"id\":44,\"min_value\":31500,\"max_value\":32099},{\"id\":45,\"min_value\":32100,\"max_value\":32699},{\"id\":46,\"min_value\":32700,\"max_value\":33199},{\"id\":47,\"min_value\":33200,\"max_value\":33799},{\"id\":48,\"min_value\":33800,\"max_value\":34399},{\"id\":49,\"min_value\":34400,\"max_value\":34999},{\"id\":50,\"min_value\":35000,\"max_value\":35599},{\"id\":51,\"min_value\":35600,\"max_value\":36199},{\"id\":52,\"min_value\":36200,\"max_value\":36799},{\"id\":53,\"min_value\":36800,\"max_value\":37499},{\"id\":54,\"min_value\":37500,\"max_value\":38099},{\"id\":55,\"min_value\":38100,\"max_value\":38699},{\"id\":56,\"min_value\":38700,\"max_value\":39399},{\"id\":57,\"min_value\":39400,\"max_value\":39999},{\"id\":58,\"min_value\":40000,\"max_value\":40699},{\"id\":59,\"min_value\":40700,\"max_value\":41299},{\"id\":60,\"min_value\":41300,\"max_value\":41999},{\"id\":61,\"min_value\":42000,\"max_value\":42699},{\"id\":62,\"min_value\":42700,\"max_value\":43399},{\"id\":63,\"min_value\":43400,\"max_value\":43999},{\"id\":64,\"min_value\":44000,\"max_value\":44699},{\"id\":65,\"min_value\":44700,\"max_value\":45399},{\"id\":66,\"min_value\":45400,\"max_value\":46199},{\"id\":67,\"min_value\":46200,\"max_value\":46899},{\"id\":68,\"min_value\":46900,\"max_value\":47599},{\"id\":69,\"min_value\":47600,\"max_value\":48299},{\"id\":70,\"min_value\":48300,\"max_value\":48999},{\"id\":71,\"min_value\":49000,\"max_value\":49799},{\"id\":72,\"min_value\":49800,\"max_value\":50499},{\"id\":73,\"min_value\":50500,\"max_value\":51299},{\"id\":74,\"min_value\":51300,\"max_value\":51999},{\"id\":75,\"min_value\":52000,\"max_value\":52799},{\"id\":76,\"min_value\":52800,\"max_value\":53599},{\"id\":77,\"min_value\":53600,\"max_value\":54399},{\"id\":78,\"min_value\":54400,\"max_value\":55199},{\"id\":79,\"min_value\":55200,\"max_value\":55899},{\"id\":80,\"min_value\":55900,\"max_value\":56699},{\"id\":81,\"min_value\":56700,\"max_value\":57499},{\"id\":82,\"min_value\":57500,\"max_value\":58399},{\"id\":83,\"min_value\":58400,\"max_value\":59199},{\"id\":84,\"min_value\":59200,\"max_value\":59999},{\"id\":85,\"min_value\":60000,\"max_value\":60799},{\"id\":86,\"min_value\":60800,\"max_value\":61699},{\"id\":87,\"min_value\":61700,\"max_value\":62499},{\"id\":88,\"min_value\":62500,\"max_value\":63399},{\"id\":89,\"min_value\":63400,\"max_value\":64199},{\"id\":90,\"min_value\":64200,\"max_value\":65099},{\"id\":91,\"min_value\":65100,\"max_value\":65999},{\"id\":92,\"min_value\":66000,\"max_value\":66799},{\"id\":93,\"min_value\":66800,\"max_value\":67699},{\"id\":94,\"min_value\":67700,\"max_value\":68599},{\"id\":95,\"min_value\":68600,\"max_value\":69499},{\"id\":96,\"min_value\":69500,\"max_value\":70399},{\"id\":97,\"min_value\":70400,\"max_value\":71399},{\"id\":98,\"min_value\":71400,\"max_value\":99999}]")!;
        public string Rank
        {
            get => Id switch
            {
                1 => "[grey46]G[/]",
                2 => "[grey46]G+[/]",
                3 => "[mediumpurple3_1]F[/]",
                4 => "[mediumpurple3_1]F+[/]",
                5 => "[pink3]E[/]",
                6 => "[pink3]E+[/]",
                7 => "[deepskyblue3_1]D[/]",
                8 => "[deepskyblue3_1]D+[/]",
                9 => "[darkolivegreen3_1]C[/]",
                10 => "[darkolivegreen3_1]C+[/]",
                11 => "[palevioletred1]B[/]",
                12 => "[palevioletred1]B+[/]",
                13 => "[darkorange]A[/]",
                14 => "[darkorange]A+[/]",
                15 => "[lightgoldenrod2_2]S[/]",
                16 => "[lightgoldenrod2_2]S+[/]",
                17 => "[lightgoldenrod2_2]SS[/]",
                18 => "[lightgoldenrod2_2]SS+[/]",
                19 => "[mediumpurple1]U[mediumpurple2]G[/][/]",
                20 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]1[/][/]",
                21 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]2[/][/]",
                22 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]3[/][/]",
                23 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]4[/][/]",
                24 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]5[/][/]",
                25 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]6[/][/]",
                26 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]7[/][/]",
                27 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]8[/][/]",
                28 => "[mediumpurple1]U[mediumpurple2]G[/][purple_2]9[/][/]",
                29 => "[mediumpurple1]U[mediumpurple2]F[/][/]",
                30 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]1[/][/]",
                31 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]2[/][/]",
                32 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]3[/][/]",
                33 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]4[/][/]",
                34 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]5[/][/]",
                35 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]6[/][/]",
                36 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]7[/][/]",
                37 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]8[/][/]",
                38 => "[mediumpurple1]U[mediumpurple2]F[/][purple_2]9[/][/]",
                39 => "[mediumpurple1]U[mediumpurple2]E[/][/]",
                40 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]1[/][/]",
                41 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]2[/][/]",
                42 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]3[/][/]",
                43 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]4[/][/]",
                44 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]5[/][/]",
                45 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]6[/][/]",
                46 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]7[/][/]",
                47 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]8[/][/]",
                48 => "[mediumpurple1]U[mediumpurple2]E[/][purple_2]9[/][/]",
                49 => "[mediumpurple1]U[mediumpurple2]D[/][/]",
                50 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]1[/][/]",
                51 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]2[/][/]",
                52 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]3[/][/]",
                53 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]4[/][/]",
                54 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]5[/][/]",
                55 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]6[/][/]",
                56 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]7[/][/]",
                57 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]8[/][/]",
                58 => "[mediumpurple1]U[mediumpurple2]D[/][purple_2]9[/][/]",
                59 => "[mediumpurple1]U[mediumpurple2]C[/][/]",
                60 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]1[/][/]",
                61 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]2[/][/]",
                62 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]3[/][/]",
                63 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]4[/][/]",
                64 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]5[/][/]",
                65 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]6[/][/]",
                66 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]7[/][/]",
                67 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]8[/][/]",
                68 => "[mediumpurple1]U[mediumpurple2]C[/][purple_2]9[/][/]",
                69 => "[mediumpurple1]U[mediumpurple2]B[/][/]",
                70 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]1[/][/]",
                71 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]2[/][/]",
                72 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]3[/][/]",
                73 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]4[/][/]",
                74 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]5[/][/]",
                75 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]6[/][/]",
                76 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]7[/][/]",
                77 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]8[/][/]",
                78 => "[mediumpurple1]U[mediumpurple2]B[/][purple_2]9[/][/]",
                79 => "[mediumpurple1]U[mediumpurple2]A[/][/]",
                80 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]1[/][/]",
                81 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]2[/][/]",
                82 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]3[/][/]",
                83 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]4[/][/]",
                84 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]5[/][/]",
                85 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]6[/][/]",
                86 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]7[/][/]",
                87 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]8[/][/]",
                88 => "[mediumpurple1]U[mediumpurple2]A[/][purple_2]9[/][/]",
                89 => "[mediumpurple1]U[mediumpurple2]S[/][/]",
                90 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]1[/][/]",
                91 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]2[/][/]",
                92 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]3[/][/]",
                93 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]4[/][/]",
                94 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]5[/][/]",
                95 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]6[/][/]",
                96 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]7[/][/]",
                97 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]8[/][/]",
                98 => "[mediumpurple1]U[mediumpurple2]S[/][purple_2]9[/][/]",
                _ => "[mediumpurple1]US9[mediumpurple2]以上[/][/]"
            };
        }
        [JsonProperty("id")]
        public int Id { get; set; }
        /// <summary>
        /// 满足该评分所需的最低评价点
        /// </summary>
        [JsonProperty("min_value")]
        public int Min { get; set; }
        /// <summary>
        /// 满足该评分所需的最高评价点
        /// </summary>
        [JsonProperty("max_value")]
        public int Max { get; set; }
    }
}
