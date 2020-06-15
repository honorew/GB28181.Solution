//-----------------------------------------------------------------------------
// Filename: SIPnonInviteTransaction.cs
//
// Description: SIP Transaction for all non-INVITE transactions where no dialog is required.
// 
// History:
// 18 May 2008	Aaron Clauson	Created.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//



#if UNITTEST
using NUnit.Framework;
#endif

using SIPSorcery.SIP;

namespace GB28181
{
    public class SIPNonInviteTransaction : SIPTransaction
	{
      //  private SIPSorcery.SIP.SIPTransport sIPTransport;
     //   private SIPRequest sipRequest;
     //   private SIPEndPoint dstEndPoint;

        public event SIPTransactionResponseReceivedDelegate NonInviteTransactionInfoResponseReceived;
        public event SIPTransactionResponseReceivedDelegate NonInviteTransactionFinalResponseReceived;
        public event SIPTransactionTimedOutDelegate NonInviteTransactionTimedOut;
        public event SIPTransactionRequestReceivedDelegate NonInviteRequestReceived;
        public event SIPTransactionRequestRetransmitDelegate NonInviteTransactionRequestRetransmit;

        public SIPNonInviteTransaction(SIPTransport sipTransport, SIPRequest sipRequest, SIPEndPoint dstEndPoint, SIPEndPoint localSIPEndPoint, SIPEndPoint outboundProxy)
            : base(sipTransport, sipRequest, dstEndPoint, localSIPEndPoint, outboundProxy)
        {
            TransactionType = SIPTransactionTypesEnum.NonInvite;
            TransactionRequestReceived += SIPNonInviteTransaction_TransactionRequestReceived;
            TransactionInformationResponseReceived += SIPNonInviteTransaction_TransactionInformationResponseReceived;
            TransactionFinalResponseReceived += SIPNonInviteTransaction_TransactionFinalResponseReceived;
            TransactionTimedOut += SIPNonInviteTransaction_TransactionTimedOut;
            TransactionRemoved += SIPNonInviteTransaction_TransactionRemoved;
            TransactionRequestRetransmit += SIPNonInviteTransaction_TransactionRequestRetransmit;
        }



        private void SIPNonInviteTransaction_TransactionRemoved(SIPTransaction transaction)
        {
            // Remove all event handlers.
            NonInviteTransactionInfoResponseReceived = null;
            NonInviteTransactionFinalResponseReceived = null;
            NonInviteTransactionTimedOut = null;
            NonInviteRequestReceived = null;
        }

        private void SIPNonInviteTransaction_TransactionTimedOut(SIPTransaction sipTransaction)
        {
            NonInviteTransactionTimedOut?.Invoke(this);
        }

        private void SIPNonInviteTransaction_TransactionRequestReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPRequest sipRequest)
        {
            NonInviteRequestReceived?.Invoke(localSIPEndPoint, remoteEndPoint, this, sipRequest);
        }

        private void SIPNonInviteTransaction_TransactionInformationResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            NonInviteTransactionInfoResponseReceived?.Invoke(localSIPEndPoint, remoteEndPoint, this, sipResponse);
        }

        private void SIPNonInviteTransaction_TransactionFinalResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            NonInviteTransactionFinalResponseReceived?.Invoke(localSIPEndPoint, remoteEndPoint, this, sipResponse);
        }

        private void SIPNonInviteTransaction_TransactionRequestRetransmit(SIPTransaction sipTransaction, SIPRequest sipRequest, int retransmitNumber)
        {
            NonInviteTransactionRequestRetransmit?.Invoke(sipTransaction, sipRequest, retransmitNumber);
        }
    }
}
