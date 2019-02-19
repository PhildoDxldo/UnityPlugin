// TODO(@jackson): Check version

/***
 * Original Author: @Programmer of StackOverflow (https://stackoverflow.com/users/3785314/programmer)
 * Original URL: https://stackoverflow.com/a/50690420 (Accessed: 2019/02/17)
 *
 * This class acts as a replacement for the DownloadHandlerFile class found in Unity Versions 2017.2
 * and above. It is largely based on the StackOverflow answer found at the above URL, but adapted for
 * the needs of the mod.io Unity Plugin.
 *
 ***/

using System;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace ModIO
{
    public class DownloadHandlerFile : DownloadHandlerScript
    {
        // ---------[ CONSTANTS ]---------
        // TODO(@jackson): TEST THIS!! (Does this limit the amount of bytes received?)
        private const int BUFFER_SIZE = 1024^2;

        // ---------[ FIELDS ]---------
        public bool removeFileOnAbort = false;

        private FileStream m_fileStream = null;
        private bool m_completed = false;
        private string m_filePath = string.Empty;
        private int m_bytesDownloaded = 0;
        private int m_contentLength = -1;

        // ---------[ INITIALIZATION ]---------
        public DownloadHandlerFile(string filePath)
            : base(new byte[BUFFER_SIZE])
        {
            Debug.Assert(!String.IsNullOrEmpty(filePath));

            m_filePath = filePath;

            try
            {
                if(IOUtilities.CreateDirectory(Path.GetDirectoryName(filePath)))
                {
                    m_fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                }
            }
            catch (Exception e)
            {
                m_fileStream = null;

                string warningInfo = ("[mod.io] Failed to open file stream."
                                      + "\nFile: " + filePath + "\n\n");

                Debug.LogWarning(warningInfo
                                 + Utility.GenerateExceptionDebugString(e));
            }
        }

        // ---------[ DownloadHandler interface ]---------
        protected override void CompleteContent()
        {
            if(m_fileStream != null)
            {
                m_fileStream.Close();
                m_fileStream = null;
            }

            base.CompleteContent();
        }

        protected override byte[] GetData() { return null; }

        protected override float GetProgress()
        {
            if(m_contentLength <= 0)
            {
                return 0f;
            }

            return (float)m_bytesDownloaded / (float)m_contentLength;
        }

        protected override string GetText() { return null; }

        // Called when a Content-Length header is received from the server.
        protected override void ReceiveContentLength(int contentLength)
        {
            m_contentLength = contentLength;
            base.ReceiveContentLength(contentLength);
        }

        // Called once per frame when data has been received from the network.
        protected override bool ReceiveData(byte[] bytesReceived, int dataLength)
        {
            if (bytesReceived == null || bytesReceived.Length < 1)
            {
                return false;
            }

            if(m_fileStream != null)
            {
                try
                {
                    m_fileStream.Write(bytesReceived, 0, dataLength);
                }
                catch (Exception e)
                {
                    if(removeFileOnAbort)
                    {
                        IOUtilities.DeleteFile(m_filePath);
                    }

                    try
                    {
                        m_fileStream.Close();
                    }
                    finally
                    {
                        m_fileStream = null;
                    }

                    string warningInfo = ("[mod.io] Failed to write downloading file."
                                          + "\nFile: " + m_filePath + "\n\n");

                    Debug.LogWarning(warningInfo
                                     + Utility.GenerateExceptionDebugString(e));

                }
            }

            return true;
        }
    }
}
