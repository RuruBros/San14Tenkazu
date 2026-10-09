namespace San14Tenkazu;

internal static class CityNames
{
    // Extracted in city ID order from the running Korean game text table.
    internal static readonly string[] ById =
    [
        "",
        "양평",
        "북평",
        "계",
        "진양",
        "남피",
        "업",
        "평원",
        "북해",
        "하비",
        "소패",
        "광릉",
        "수춘",
        "여강",
        "복양",
        "진류",
        "허창",
        "여남",
        "낙양",
        "완",
        "장안",
        "상용",
        "안정",
        "천수",
        "무위",
        "건업",
        "오",
        "회계",
        "시상",
        "건안",
        "남해",
        "교지",
        "강하",
        "신야",
        "양양",
        "강릉",
        "장사",
        "무릉",
        "계양",
        "영릉",
        "영안",
        "한중",
        "자동",
        "강주",
        "성도",
        "건녕",
        "운남"
    ];

    // 명소 (famous places) by their ID 1~100 (map tile +0x1E), in the order of the game's own
    // "명승발전 최대·..." messages, which matched the tiles (12 and 13, 은허 and 유리성, lie in 탕음).
    internal static readonly string[] Landmarks =
    [
        "",
        "광우사", "갈석산", "구선입해처", "도원", "만리장성", "진사", "항산", "오대산", "평요고성", "발해",
        "형수호", "은허", "유리성", "무령총대", "구구산", "기산", "성산두", "낭야대", "홍택호", "희마대",
        "제장성", "공묘", "망탕산", "한구", "패왕사", "소호", "천주산", "척성", "태산", "취대",
        "정주상도", "파릉교", "초장성", "백마사", "삼문협", "이리두하도", "숭산", "노군산", "호구폭포", "형자관",
        "진시황병마용", "여궁", "화산", "무릉", "황제릉", "종남산", "무당산", "진장성", "공동산", "모가평유적",
        "하서회랑", "뇌대한묘", "현무호", "구화산", "황산", "호구", "태호", "감로사", "서호", "전당강",
        "보타산", "안탕산", "여산", "적벽", "파양호", "황학루", "무이산", "백운산", "남월문왕묘", "계평서산",
        "백아대", "운몽택", "장형묘", "단계", "현산", "신농가", "옥천사", "서릉협", "마왕퇴한묘", "멱라강",
        "동정호", "무릉원", "범정산", "단하산", "형산", "이강", "무협", "구당협", "한대", "석문잔도",
        "구채구", "황룡채지", "검문촉도", "홍애동", "도강언", "아미산", "고촉국유적", "황과수폭포", "운남석림", "전지"
    ];

    private static readonly string[] GateNames = ["호관", "호뢰관", "동관", "함곡관", "무관", "양평관", "백수관", "검각", "부수관", "면죽관"];
    private static readonly string[] TribeNames = ["오환", "선비", "강", "산월", "남만"];

    // An officer's base (officer +0xE2): a city, a tribe's home or a gate.
    internal static string BaseName(int id) => id switch
    {
        >= 1 and <= GameMemory.CityCount => ById[id],
        >= GameMemory.TribeFirst and <= GameMemory.TribeLast => TribeNames[id - GameMemory.TribeFirst],
        >= GameMemory.GateFirst and <= GameMemory.GateLast => GateNames[id - GameMemory.GateFirst],
        0 => "",
        _ => $"거점 {id}"
    };
}
