﻿//
// DocumentRegistry.cs
//
// Author:
//       Mike Krüger <mikkrg@microsoft.com>
//
// Copyright (c) 2018 Microsoft
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.IO;


using MonoDevelop.Core;
using Services = MonoDevelop.Projects.Services;
using MonoDevelop.Ide;
using MonoDevelop.Ide.TypeSystem;
using MonoDevelop.Ide.Editor;
using System.Linq;
using Gtk;
using MonoDevelop.Components;

namespace MonoDevelop.Ide.Gui
{
	static class DocumentRegistry
	{
		readonly static List<DocumentInfo> openFiles = new List<DocumentInfo> ();

		readonly static FileSystemWatcher fileSystemWatcher;

		public static IEnumerable<Document> OpenFiles {
			get {
				return openFiles.Select(di => di.Document);
			}
		}

		static DocumentRegistry ()
		{
			fileSystemWatcher = new FileSystemWatcher ();
			fileSystemWatcher.Created += (s, e) => Runtime.RunInMainThread (() => OnFileChanged (s, e));
			fileSystemWatcher.Changed += (s, e) => Runtime.RunInMainThread (() => OnFileChanged (s, e));

			FileService.FileCreated += HandleFileServiceChange;
			FileService.FileChanged += HandleFileServiceChange;
		}

		static void HandleFileServiceChange (object sender, FileEventArgs e)
		{
			bool foundOneChange = false;
			foreach (var file in e) {
				if (skipFiles.Contains (file.FileName)) {
					skipFiles.Remove (file.FileName);
					continue;
				}
				foreach (var view in openFiles) {
					if (SkipView (view.Document) || !string.Equals (view.Document.FileName, file.FileName, FilePath.PathComparison))
						continue;
					if (!view.Document.IsDirty)
						view.Document.Reload ();
					else
						foundOneChange = true;
				}
			}

			if (foundOneChange)
				CommitViewChange (GetAllChangedFiles ());
		}
		static void OnFileChanged (object sender, FileSystemEventArgs e)
		{
			if (e.ChangeType == WatcherChangeTypes.Changed || e.ChangeType == WatcherChangeTypes.Created)
				CheckFileChange (e.FullPath);
		}

		internal static bool SkipView (Document view)
		{
			return !view.IsFile || view.IsUntitled ;
		}


		static void CheckFileChange (string fileName)
		{
			if (skipFiles.Contains (fileName)) {
				skipFiles.Remove (fileName);
				return;
			}

			var changedViews = new List<DocumentInfo> ();
			foreach (var view in openFiles) {
				if (SkipView (view.Document))
					continue;
				if (string.Equals (view.Document.FileName, fileName, FilePath.PathComparison)) {
					if (view.LastSaveTimeUtc == File.GetLastWriteTimeUtc (fileName))
						continue;
					if (!view.Document.IsDirty)
						view.Document.Reload ();
					else
						changedViews.Add (view);
				}
			}
			CommitViewChange (changedViews);
		}


		static void CommitViewChange (List<DocumentInfo> changedDocuments)
		{
			if (changedDocuments.Count == 0)
				return;
			if (changedDocuments.Count == 1) {
				var presenter = changedDocuments [0].Document.PrimaryView.BaseContent as ViewContent;
				if (presenter != null) {
					ShowFileChangedWarning (presenter, false);
				} else {
					changedDocuments [0].Document.Reload ();
				}
				if (changedDocuments [0].Document != IdeApp.Workbench.ActiveDocument)
					changedDocuments [0].Document.Select ();
			} else {
				foreach (var view in changedDocuments) {
					var presenter = view.Document.PrimaryView.BaseContent as ViewContent;
					if (presenter != null) {
						ShowFileChangedWarning (presenter, true);
					} else {
						view.Document.Reload ();
					}
				}
				if (!changedDocuments.Any (di => di.Document == IdeApp.Workbench.ActiveDocument))
					changedDocuments [0].Document.Select ();
			}
		}

		public static void Add (Document sourceEditorView)
		{
			openFiles.Add (new DocumentInfo (sourceEditorView));
		}

		public static void Remove (Document sourceEditorView)
		{
			for (int i = 0; i < openFiles.Count; i++) {
				var info = openFiles [i];
				if (info.Document == sourceEditorView) {
					openFiles.RemoveAt (i);
					info.Dispose ();
					break;
				}
			}
			// UpdateEolMessages ();
		}

		public static void IgnoreAllChangedFiles ()
		{
			foreach (var view in GetAllChangedFiles ()) {
				view.LastSaveTimeUtc = File.GetLastWriteTime (view.Document.FileName);
				var presenter = view.Document.PrimaryView.BaseContent as ViewContent;
				if (presenter != null)
					presenter.InfoBar = null;
				view.Document.Window.ShowNotification = false;
			}
		}

		public static void ReloadAllChangedFiles ()
		{
			foreach (var view in GetAllChangedFiles ()) {
				var presenter = view.Document.PrimaryView.BaseContent as ViewContent;
				presenter?.Reload ();
				view.Document.Window.ShowNotification = false;
			}
		}

		static List<DocumentInfo> GetAllChangedFiles ()
		{
			var changedViews = new List<DocumentInfo> ();
			foreach (var view in openFiles) {
				if (SkipView (view.Document))
					continue;
				if (view.LastSaveTimeUtc == File.GetLastWriteTimeUtc (view.Document.FileName))
					continue;
				if (!view.Document.IsDirty)
					view.Document.Reload ();
				else
					changedViews.Add (view);
			}
			return changedViews;
		}


		class DocumentInfo : IDisposable
		{
			public Document Document { get; private set; } 
			public DateTime LastSaveTimeUtc { get; set; }

			public DocumentInfo (Document doc)
			{
				this.Document = doc;
				LastSaveTimeUtc = DateTime.UtcNow;
				doc.Saved += Doc_Saved;
				doc.Reloaded += Doc_Saved;
			}


			void Doc_Saved (object sender, EventArgs e)
			{
				try {
					LastSaveTimeUtc = File.GetLastWriteTimeUtc (Document.FileName);
				} catch (Exception ex) {
					LoggingService.LogError ("Error while getting last write time.", ex);
					LastSaveTimeUtc = DateTime.UtcNow;
				}
			}

			public void Dispose ()
			{
				Document.Saved -= Doc_Saved;
				Document.Reloaded -= Doc_Saved;
			}
		}

		static List<string> skipFiles = new List<string> ();
		internal static void SkipNextChange (string fileName)
		{
			if (!skipFiles.Contains (fileName))
				skipFiles.Add (fileName);
		}


		static void ShowFileChangedWarning (ViewContent viewContent, bool multiple)
		{
			viewContent.InfoBar = null;

			var infoBar = new MonoDevelop.Components.InfoBar (MessageType.Warning);
			infoBar.SetMessageLabel (GettextCatalog.GetString (
				"<b>The file \"{0}\" has been changed outside of {1}.</b>\n" +
				"Do you want to keep your changes, or reload the file from disk?",
				ViewContent.EllipsizeMiddle (viewContent.ContentName, 50), BrandingService.ApplicationName));

			var b1 = new Button (GettextCatalog.GetString ("_Reload from disk"));
			b1.Image = new ImageView (Gtk.Stock.Refresh, IconSize.Button);
			b1.Clicked += async delegate {
				await viewContent.Reload ();
				viewContent.WorkbenchWindow.SelectWindow ();
				viewContent.InfoBar = null;
			};
			infoBar.ActionArea.Add (b1);

			var b2 = new Button (GettextCatalog.GetString ("_Keep changes"));
			b2.Image = new ImageView (Gtk.Stock.Cancel, IconSize.Button);
			b2.Clicked += delegate {
				viewContent.InfoBar = null;
				viewContent.WorkbenchWindow.ShowNotification = false;
			};
			infoBar.ActionArea.Add (b2);

			if (multiple) {
				var b3 = new Button (GettextCatalog.GetString ("_Reload all"));
				b3.Image = new ImageView (Gtk.Stock.Cancel, IconSize.Button);
				b3.Clicked += delegate {
					DocumentRegistry.ReloadAllChangedFiles ();
					viewContent.InfoBar = null;
				};
				infoBar.ActionArea.Add (b3);

				var b4 = new Button (GettextCatalog.GetString ("_Ignore all"));
				b4.Image = new ImageView (Gtk.Stock.Cancel, IconSize.Button);
				b4.Clicked += delegate {
					DocumentRegistry.IgnoreAllChangedFiles ();
					viewContent.InfoBar = null;
				};
				infoBar.ActionArea.Add (b4);
			}
			viewContent.InfoBar = infoBar;
		}

	}
}