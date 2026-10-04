import base64,gzip,hashlib,json,sys,tempfile,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts'))
from issue30_decode_regression_export import decode
class ExportDecodeTests(unittest.TestCase):
    def report(self):return {'plan':{'evaluationId':'issue30-hardening-20261003-v1','observedRegressionSet':True,'independentHoldout':False},'completed':True,'rows':[],'aiAudits':[]}
    def log(self,checksum=None,count=1):
        data=json.dumps(self.report()).encode();encoded=base64.b64encode(gzip.compress(data,mtime=0)).decode()
        chunk={'index':0,'count':count,'sha256':checksum or hashlib.sha256(data).hexdigest(),'data':encoded}
        lines=['REGRESSION_CHUNK '+line if i==0 else line for i,line in enumerate(json.dumps(chunk,indent=2).splitlines())]
        lines=[line.replace('+',r'\+') if '"data"' in line else line for line in lines]
        return '\n'.join('{"Log":"F '+line+'"}' for line in lines),data
    def run_decode(self,text):
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'log';path.write_text(text);return decode(path)
    def test_log_formatter_escapes_are_normalized_and_checksum_is_verified(self):
        log,data=self.log();decoded,meta=self.run_decode(log);self.assertEqual(decoded,data);self.assertEqual(meta['sha256'],hashlib.sha256(data).hexdigest())
    def test_independent_diagnostic_requires_explicit_scope(self):
        report={'plan':{'evaluationId':'issue30-independent-20261004-v2','observedRegressionSet':False,'independentHoldout':True},'units':[],'aiAudits':[],'inferenceSent':False}
        data=json.dumps(report).encode();chunk={'index':0,'count':1,'sha256':hashlib.sha256(data).hexdigest(),'data':base64.b64encode(gzip.compress(data,mtime=0)).decode()}
        log='{"Log":"F REGRESSION_CHUNK '+json.dumps(chunk)+'"}'
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'log';path.write_text(log)
            with self.assertRaisesRegex(ValueError,'scope'):decode(path)
            decoded,meta=decode(path,'issue30-independent-20261004-v2',False)
            self.assertEqual(decoded,data);self.assertIsNone(meta['reportComplete'])

    def test_corrupt_checksum_is_rejected(self):
        log,_=self.log('0'*64)
        with self.assertRaisesRegex(ValueError,'checksum'):self.run_decode(log)
    def test_missing_chunk_is_rejected(self):
        log,_=self.log(count=2)
        with self.assertRaisesRegex(ValueError,'Missing'):self.run_decode(log)
if __name__=='__main__':unittest.main()
