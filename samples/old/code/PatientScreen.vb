' The patient screen's saving code, from the 2004 program. Invented sample.
Imports System.Data.SqlClient

Public Class PatientScreen
    Private ReadOnly _db As SqlConnection

    Public Sub New(db As SqlConnection)
        _db = db
    End Sub

    Public Sub SavePatient(name As String, birth As String, sex As String, payer As String, active As Boolean, phone As String, user As String)
        If name.IndexOf(","c) < 0 Then
            Throw New ArgumentException("Enter the name as LAST, FIRST.")
        End If
        If Not IsDate(birth) Then
            Throw New ArgumentException("Birth date must be MM/DD/YYYY.")
        End If
        If sex <> "M" AndAlso sex <> "F" AndAlso sex <> "U" Then
            Throw New ArgumentException("Sex is M, F or U.")
        End If
        Dim cmd As New SqlCommand("INSERT INTO dbo.tblPatient (PatName, DOB, Sex, PayerCode, Active, Phone, EnteredBy) VALUES (@n, @d, @s, @p, @a, @ph, @u)", _db)
        cmd.Parameters.AddWithValue("@n", name.ToUpper())
        cmd.Parameters.AddWithValue("@d", birth)
        cmd.Parameters.AddWithValue("@s", sex)
        cmd.Parameters.AddWithValue("@p", payer)
        cmd.Parameters.AddWithValue("@a", If(active, "Y", "N"))
        cmd.Parameters.AddWithValue("@ph", phone)
        cmd.Parameters.AddWithValue("@u", user)
        cmd.ExecuteNonQuery()
    End Sub

    Public Sub DeletePatient(id As Integer)
        Dim cmd As New SqlCommand("UPDATE dbo.tblPatient SET Deleted = 1 WHERE PatID = @id", _db)
        cmd.Parameters.AddWithValue("@id", id)
        cmd.ExecuteNonQuery()
    End Sub

    Public Function FindPatients(text As String) As DataTable
        Dim da As New SqlDataAdapter("SELECT PatID, PatName, DOB, Sex, PayerCode, Phone FROM dbo.tblPatient WHERE Deleted = 0 AND PatName LIKE @t", _db)
        da.SelectCommand.Parameters.AddWithValue("@t", text & "%")
        Dim t As New DataTable()
        da.Fill(t)
        Return t
    End Function
End Class
