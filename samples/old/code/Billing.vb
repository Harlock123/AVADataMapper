' Charges and claims, from the 2009 billing module. Invented sample.
Imports System.Data.SqlClient

Public Class Billing
    Private ReadOnly _db As SqlConnection

    Public Sub New(db As SqlConnection)
        _db = db
    End Sub

    Public Sub PostCharge(patient As Integer, visit As Integer, procedureCode As String, amount As Decimal)
        If amount = 0 Then
            Throw New ArgumentException("A charge can't be zero.")
        End If
        Dim copay As Decimal = amount * CopayPercent(patient) / 100D
        Dim cmd As New SqlCommand("INSERT INTO dbo.tblCharge (PatID, VisitID, ProcCode, Amt, Copay, PostedOn) VALUES (@p, @v, @c, @a, @cp, GETDATE())", _db)
        cmd.Parameters.AddWithValue("@p", patient)
        cmd.Parameters.AddWithValue("@v", visit)
        cmd.Parameters.AddWithValue("@c", procedureCode)
        cmd.Parameters.AddWithValue("@a", amount)
        cmd.Parameters.AddWithValue("@cp", copay)
        cmd.ExecuteNonQuery()
    End Sub

    Private Function CopayPercent(patient As Integer) As Decimal
        Dim cmd As New SqlCommand("SELECT ISNULL(y.CopayPct, 0) FROM dbo.tblPatient p JOIN dbo.tblPayer y ON y.PayerCode = p.PayerCode WHERE p.PatID = @p", _db)
        cmd.Parameters.AddWithValue("@p", patient)
        Return CDec(cmd.ExecuteScalar())
    End Function

    Public Sub EnterClaim(number As String, patient As Integer, amount As Decimal)
        Dim cmd As New SqlCommand("INSERT INTO dbo.tblClaim (ClaimNo, PatID, Amt, Status, EnteredOn) VALUES (@n, @p, @a, 'OPEN', GETDATE())", _db)
        cmd.Parameters.AddWithValue("@n", number)
        cmd.Parameters.AddWithValue("@p", patient)
        cmd.Parameters.AddWithValue("@a", amount)
        cmd.ExecuteNonQuery()
    End Sub

    Public Sub SetClaimStatus(number As String, status As String)
        Select Case status
            Case "OPEN", "SENT", "PAID", "DENIED", "VOID"
            Case Else
                Throw New ArgumentException("Unknown claim status.")
        End Select
        Dim cmd As New SqlCommand("UPDATE dbo.tblClaim SET Status = @s WHERE ClaimNo = @n", _db)
        cmd.Parameters.AddWithValue("@s", status)
        cmd.Parameters.AddWithValue("@n", number)
        cmd.ExecuteNonQuery()
    End Sub

    Public Sub RecordVisit(patient As Integer, clinician As String, reason As String)
        Dim cmd As New SqlCommand("INSERT INTO dbo.tblVisit (PatID, VisitDt, Clinician, Reason) VALUES (@p, GETDATE(), @c, @r)", _db)
        cmd.Parameters.AddWithValue("@p", patient)
        cmd.Parameters.AddWithValue("@c", clinician)
        cmd.Parameters.AddWithValue("@r", reason)
        cmd.ExecuteNonQuery()
    End Sub
End Class
