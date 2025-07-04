﻿/*******************************************************************************
*
*  (C) COPYRIGHT AUTHORS, 2024 - 2025
*
*  TITLE:       ABOUTFORM.CS
*
*  VERSION:     1.00
*
*  DATE:        14 Apr 2025
*
* THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF
* ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED
* TO THE IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
* PARTICULAR PURPOSE.
*
*******************************************************************************/
namespace WinDepends;

internal partial class AboutForm : Form
{
    readonly bool escKeyEnabled;

    public AboutForm(bool bEscKeyEnabled)
    {
        InitializeComponent();
        escKeyEnabled = bEscKeyEnabled;
    }

    private void AboutForm_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && escKeyEnabled)
        {
            this.Close();
        }
    }

    private void AboutForm_Load(object sender, EventArgs e)
    {
        Text = $"About {CConsts.ProgramName}";
        AboutVersionLabel.Text = $"Version: {CConsts.VersionMajor}.{CConsts.VersionMinor}.{CConsts.VersionRevision}.{CConsts.VersionBuild}";
        AboutNameLabel.Text = $"{CConsts.ProgramName} for Windows 10/11";
        AboutCopyrightLabel.Text = CConsts.CopyrightString;
        AboutBuildLabel.Text = $"Build date: {Properties.Resources.BuildDate}";
        AboutAssemblyLabel.Text = $"NET Framework version: {CUtils.GetRunningFrameworkVersion()}";
        AboutOSLabel.Text = System.Environment.OSVersion.ToString();
    }

    private void LinkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        linkLabel1.LinkVisited = true;
        CUtils.RunExternalCommand(CConsts.WinDependsHome, true);
    }

    private void LinkLabel2_Clicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        linkLabel2.LinkVisited = true;
        CUtils.RunExternalCommand(CConsts.DependsHome, true);
    }
}
