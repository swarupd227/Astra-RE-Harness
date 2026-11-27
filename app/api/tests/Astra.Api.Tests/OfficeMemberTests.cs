using Astra.Api.Endpoints;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Home says "VBA" for VB6-grammar code that lives inside an Office document, as the project list does.</summary>
public class OfficeMemberTests
{
    [Theory]
    [InlineData("Rating.xlsm/Module1.bas", true)]
    [InlineData("Quotes/Book.xls/Sheet1.cls", true)]
    [InlineData("Report.docm/ThisDocument.cls", true)]
    [InlineData("Deck.PPTM/Module1.bas", true)]
    [InlineData("Db.accdb/Form_Main.cls", true)]
    [InlineData("src/Inventory.bas", false)]
    [InlineData("docs/readme.doc", false)]
    [InlineData("Forms/frmXlsImport.frm", false)]
    public void OfficeMember_MatchesFilesInsideOfficeDocuments(string path, bool expected) =>
        Assert.Equal(expected, ConversationEndpoints.OfficeMember.IsMatch(path));
}
